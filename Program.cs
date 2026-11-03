using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Porta: usa PORT env (Railway/Render) oppure 5100 locale
var port = Environment.GetEnvironmentVariable("PORT") ?? "5100";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Registra il client BigQuery come singleton
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["BigQuery:ProjectId"]!;

    // 1) Prova credenziali da variabile d'ambiente (per deploy cloud)
    var credJson = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON");
    var credB64  = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON_B64");
    if (string.IsNullOrEmpty(credJson) && !string.IsNullOrEmpty(credB64))
        credJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(credB64));
    if (!string.IsNullOrEmpty(credJson))
    {
        var credential = GoogleCredential.FromJson(credJson);
        return BigQueryClient.Create(projectId, credential);
    }

    // 2) Fallback: leggi da file locale (sviluppo)
    var credentialsPath = config["BigQuery:CredentialsPath"]!;
    var fullPath = Path.IsPathRooted(credentialsPath)
        ? credentialsPath
        : Path.Combine(AppContext.BaseDirectory, credentialsPath);

    if (!File.Exists(fullPath))
        fullPath = Path.Combine(Directory.GetCurrentDirectory(), credentialsPath);

    var credFile = GoogleCredential.FromFile(fullPath);
    return BigQueryClient.Create(projectId, credFile);
});

builder.Services.AddHttpClient();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Nome del cliente mostrato in pagina, nella guida e alla richiesta di login (appsettings.json → Cliente:Nome)
var nomeCliente = app.Configuration["Cliente:Nome"];
if (string.IsNullOrWhiteSpace(nomeCliente)) nomeCliente = "Tymex";

// ========================================================
// MULTI-USER AUTH + PERMISSIONS
// ========================================================
var appUsers = new List<AppUser>();
var usersEnvJson = Environment.GetEnvironmentVariable("USERS_JSON");
if (!string.IsNullOrEmpty(usersEnvJson))
{
    try { appUsers = System.Text.Json.JsonSerializer.Deserialize<List<AppUser>>(usersEnvJson) ?? []; }
    catch { }
}
// APP_USER/APP_PASSWORD è sempre garantito (anche quando USERS_JSON è già popolato)
var singleUser = Environment.GetEnvironmentVariable("APP_USER") ?? "";
var singlePass = Environment.GetEnvironmentVariable("APP_PASSWORD") ?? "";
if (!string.IsNullOrEmpty(singleUser) && !appUsers.Any(u => u.Username.Equals(singleUser, StringComparison.OrdinalIgnoreCase)))
    appUsers.Add(new AppUser(singleUser, singlePass, "Administrator", true));

var userPermissions = new Dictionary<string, UserPermission>();
var permEnvJson = Environment.GetEnvironmentVariable("PERMISSIONS_JSON");
if (!string.IsNullOrEmpty(permEnvJson))
{
    try { userPermissions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, UserPermission>>(permEnvJson) ?? []; }
    catch { }
}

// Cache operatori da BigQuery (TTL 5 minuti)
List<OperatorBQ> operatorsCache = [];
DateTime operatorsCacheExpiry = DateTime.MinValue;
SemaphoreSlim operatorsCacheLock = new(1, 1);

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health")) { await next(); return; }
    if (appUsers.Count == 0)
    {
        // Nessun utente configurato: accesso libero, utente anonimo trattato come admin
        context.Items["CurrentUser"] = new AppUser("admin", "", "Admin", true);
        await next();
        return;
    }

    AppUser? found = null;
    var authHeader = context.Request.Headers.Authorization.ToString();
    if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        string? decoded = null;
        try
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            // Credenziali Basic malformate: trattate come accesso non autorizzato
        }

        if (decoded != null)
        {
            var parts = decoded.Split(':', 2);
            if (parts.Length == 2)
            {
                found = appUsers.FirstOrDefault(u => u.Username == parts[0] && u.Password == parts[1]);
                if (found == null)
                {
                    // Fallback: verifica tra gli operatori BigQuery
                    var bqClient = app.Services.GetRequiredService<BigQueryClient>();
                    var bqOps = await GetOperatorsAsync(bqClient);
                    var op = bqOps.FirstOrDefault(o =>
                        o.Email.Equals(parts[0], StringComparison.OrdinalIgnoreCase) &&
                        o.Password == parts[1]);
                    if (op != null)
                    {
                        // ADMIN e MANAGER vedono tutto; ASSIST vede solo se stesso
                        var isAdmin = op.Profilo.Equals("ADMIN", StringComparison.OrdinalIgnoreCase)
                                   || op.Profilo.Equals("MANAGER", StringComparison.OrdinalIgnoreCase);
                        List<string>? forced = isAdmin ? null : [op.DisplayName];
                        found = new AppUser(op.Email, op.Password, op.DisplayName, isAdmin, op.CostoOrario, forced);
                    }
                }
            }
        }
    }

    if (found != null)
    {
        context.Items["CurrentUser"] = found;
        await next();
        return;
    }

    context.Response.StatusCode = 401;
    context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{nomeCliente}\"";
    await context.Response.WriteAsJsonAsync(new { detail = "Accesso non autorizzato" });
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseDefaultFiles();
app.UseStaticFiles();

// ========================================================
// ENDPOINT BIGQUERY
// ========================================================

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.Now }))
   .WithName("HealthCheck");

// Lista tutti i dataset del progetto
app.MapGet("/api/bigquery/datasets", (BigQueryClient bq) =>
{
    try
    {
        var datasets = bq.ListDatasets().Select(d => new
        {
            d.Reference.DatasetId,
            d.FullyQualifiedId
        }).ToList();

        return Results.Ok(datasets);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore: {ex.Message}");
    }
})
.WithName("ListDatasets");

// Lista tutte le tabelle di un dataset
app.MapGet("/api/bigquery/datasets/{datasetId}/tables", (string datasetId, BigQueryClient bq) =>
{
    try
    {
        var tables = bq.ListTables(datasetId).Select(t => new
        {
            t.Reference.TableId,
            t.FullyQualifiedId,
            RowCount = t.Resource.NumRows,
            SizeMB = t.Resource.NumBytes.HasValue
                ? Math.Round((double)t.Resource.NumBytes.Value / 1024 / 1024, 2)
                : 0
        }).ToList();

        return Results.Ok(tables);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore: {ex.Message}");
    }
})
.WithName("ListTables");

// Leggi i dati di una tabella (con limite righe opzionale)
app.MapGet("/api/bigquery/datasets/{datasetId}/tables/{tableId}/rows",
    (string datasetId, string tableId, int? limit, BigQueryClient bq) =>
{
    try
    {
        var maxRows = limit ?? 100;
        var sql = $"SELECT * FROM `{bq.ProjectId}.{datasetId}.{tableId}` LIMIT {maxRows}";

        var result = bq.ExecuteQuery(sql, parameters: null);

        var schema = result.Schema.Fields.Select(f => f.Name).ToList();
        var rows = new List<Dictionary<string, object?>>();

        foreach (var row in result)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < schema.Count; i++)
            {
                dict[schema[i]] = row[schema[i]];
            }
            rows.Add(dict);
        }

        return Results.Ok(new
        {
            schema,
            totalRows = rows.Count,
            rows
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore: {ex.Message}");
    }
})
.WithName("GetTableRows");

// Schema di una tabella
app.MapGet("/api/bigquery/datasets/{datasetId}/tables/{tableId}/schema",
    (string datasetId, string tableId, BigQueryClient bq) =>
{
    try
    {
        var table = bq.GetTable(datasetId, tableId);
        var fields = table.Schema.Fields.Select(f => new
        {
            f.Name,
            Type = f.Type.ToString(),
            Mode = f.Mode.ToString(),
            f.Description
        }).ToList();

        return Results.Ok(new
        {
            table.Reference.TableId,
            table.FullyQualifiedId,
            RowCount = table.Resource.NumRows,
            fields
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore: {ex.Message}");
    }
})
.WithName("GetTableSchema");

// Esegui una query SQL personalizzata
app.MapPost("/api/bigquery/query", async (HttpRequest request, BigQueryClient bq) =>
{
    try
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        var json = System.Text.Json.JsonDocument.Parse(body);
        var sql = json.RootElement.GetProperty("sql").GetString();

        if (string.IsNullOrWhiteSpace(sql))
            return Results.BadRequest("Il campo 'sql' e' obbligatorio");

        var result = bq.ExecuteQuery(sql, parameters: null);

        var schema = result.Schema.Fields.Select(f => f.Name).ToList();
        var rows = new List<Dictionary<string, object?>>();

        foreach (var row in result)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < schema.Count; i++)
            {
                dict[schema[i]] = row[schema[i]];
            }
            rows.Add(dict);
        }

        return Results.Ok(new
        {
            schema,
            totalRows = rows.Count,
            rows
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore: {ex.Message}");
    }
})
.WithName("ExecuteQuery");

// ========================================================
// ENDPOINT COSTI ORARI (link xlsx + env var/file storage)
// ========================================================

// Storage link: 24 env var individuali (COSTI_LINK_2026_01 ... COSTI_LINK_2027_12) + file locale
// Usa /data/ se esiste (Railway Volume persistente), altrimenti directory app
var dataDir = Directory.Exists("/data") ? "/data" : AppContext.BaseDirectory;
var costiLinksFile = Path.Combine(dataDir, "costi-links.json");
var costiLinks = new Dictionary<string, string>();

// 1) Carica da env var individuali (persistenti tra deploy)
foreach (var anno in new[] { 2026, 2027 })
{
    for (int m = 1; m <= 12; m++)
    {
        var meseKey = $"{anno}-{m:D2}";
        var envName = $"COSTI_LINK_{anno}_{m:D2}";
        var val = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(val)) costiLinks[meseKey] = val;
    }
}

// 2) Overlay da file locale (aggiornamenti runtime, persistono tra restart)
if (File.Exists(costiLinksFile))
{
    try
    {
        var fileJson = File.ReadAllText(costiLinksFile);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(fileJson);
        if (parsed != null)
        {
            foreach (var kv in parsed) costiLinks[kv.Key] = kv.Value;
        }
    }
    catch { }
}

void SaveCostiLinksToFile()
{
    try { File.WriteAllText(costiLinksFile, System.Text.Json.JsonSerializer.Serialize(costiLinks)); }
    catch { }
}

// Sync link su Railway env var via GraphQL API (persiste tra deploy)
async Task SyncLinkToRailwayEnvVar(string meseKey, string link, IHttpClientFactory httpFactory)
{
    var token = Environment.GetEnvironmentVariable("RAILWAY_API_TOKEN");
    var projectId = Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID");
    var envId = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT_ID");
    var serviceId = Environment.GetEnvironmentVariable("RAILWAY_SERVICE_ID");

    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(projectId) ||
        string.IsNullOrEmpty(envId) || string.IsNullOrEmpty(serviceId)) return;

    // 2026-01 → COSTI_LINK_2026_01
    var envVarName = $"COSTI_LINK_{meseKey.Replace("-", "_")}";

    var http = httpFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var mutation = System.Text.Json.JsonSerializer.Serialize(new
    {
        query = "mutation($input: VariableUpsertInput!) { variableUpsert(input: $input) }",
        variables = new
        {
            input = new
            {
                projectId,
                environmentId = envId,
                serviceId,
                name = envVarName,
                value = link
            }
        }
    });

    var content = new StringContent(mutation, System.Text.Encoding.UTF8, "application/json");
    try { await http.PostAsync("https://backboard.railway.app/graphql/v2", content); }
    catch { } // best-effort: il file locale e' il backup
}

async Task DeleteLinkFromRailwayEnvVar(string meseKey, IHttpClientFactory httpFactory)
{
    var token = Environment.GetEnvironmentVariable("RAILWAY_API_TOKEN");
    var projectId = Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID");
    var envId = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT_ID");
    var serviceId = Environment.GetEnvironmentVariable("RAILWAY_SERVICE_ID");

    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(projectId) ||
        string.IsNullOrEmpty(envId) || string.IsNullOrEmpty(serviceId)) return;

    var envVarName = $"COSTI_LINK_{meseKey.Replace("-", "_")}";

    var http = httpFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var mutation = System.Text.Json.JsonSerializer.Serialize(new
    {
        query = "mutation($input: VariableDeleteInput!) { variableDelete(input: $input) }",
        variables = new
        {
            input = new { projectId, environmentId = envId, serviceId, name = envVarName }
        }
    });

    var content = new StringContent(mutation, System.Text.Encoding.UTF8, "application/json");
    try { await http.PostAsync("https://backboard.railway.app/graphql/v2", content); }
    catch { }
}

// GET /api/costi/links - Ritorna tutti i link salvati
app.MapGet("/api/costi/links", () =>
{
    var rows = costiLinks.Select(kv => new { meseKey = kv.Key, link = kv.Value }).OrderBy(r => r.meseKey).ToList();
    return Results.Ok(rows);
})
.WithName("GetCostiLinks");

// POST /api/costi/links - Salva/aggiorna un link per un mese
app.MapPost("/api/costi/links", async (HttpRequest request, IHttpClientFactory httpFactory) =>
{
    try
    {
        var body = await new StreamReader(request.Body).ReadToEndAsync();
        var json = System.Text.Json.JsonDocument.Parse(body);
        var meseKey = json.RootElement.GetProperty("meseKey").GetString();
        var link = json.RootElement.GetProperty("link").GetString();

        if (string.IsNullOrWhiteSpace(meseKey) || string.IsNullOrWhiteSpace(link))
            return Results.BadRequest("meseKey e link sono obbligatori");

        costiLinks[meseKey] = link;
        SaveCostiLinksToFile();

        // Sync su Railway env var in background (persiste tra deploy)
        _ = SyncLinkToRailwayEnvVar(meseKey, link, httpFactory);

        return Results.Ok(new { success = true, meseKey, link });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore: {ex.Message}");
    }
})
.WithName("SaveCostiLink");

// DELETE /api/costi/links/{meseKey} - Rimuove il link salvato per un mese
app.MapDelete("/api/costi/links/{meseKey}", async (string meseKey, IHttpClientFactory httpFactory) =>
{
    costiLinks.Remove(meseKey);
    SaveCostiLinksToFile();
    _ = DeleteLinkFromRailwayEnvVar(meseKey, httpFactory);
    return Results.Ok(new { success = true, meseKey });
})
.WithName("DeleteCostiLink");

// POST /api/proxy/fetch-xlsx - Proxy per scaricare file xlsx (evita CORS)
app.MapPost("/api/proxy/fetch-xlsx", async (HttpRequest request, IHttpClientFactory httpFactory) =>
{
    try
    {
        var body = await new StreamReader(request.Body).ReadToEndAsync();
        var json = System.Text.Json.JsonDocument.Parse(body);
        var url = json.RootElement.GetProperty("url").GetString();

        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest("Il campo 'url' e' obbligatorio");

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        var response = await http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
            return Results.Problem($"Errore download: HTTP {(int)response.StatusCode}");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        return Results.File(bytes, "application/octet-stream", "file.xlsx");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore proxy: {ex.Message}");
    }
})
.WithName("ProxyFetchXlsx");

// ========================================================
// ENDPOINT AUTH
// ========================================================

app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null) return Results.Unauthorized();

    List<string>? allowed = null;
    bool canSeeCosts = user.IsAdmin;
    if (!user.IsAdmin)
    {
        // Utenti ASSIST autenticati da BigQuery: restrizione già inclusa in ForcedAllowedOperators
        if (user.ForcedAllowedOperators != null)
            allowed = user.ForcedAllowedOperators;
        else
        {
            var perm = userPermissions.GetValueOrDefault(user.Username);
            // Se nessun permesso esplicito: vede solo i propri dati (displayName come filtro)
            allowed     = perm?.AllowedOperators ?? [user.DisplayName];
            canSeeCosts = perm?.CanSeeCosts ?? false;
        }
    }

    return Results.Ok(new { username = user.Username, displayName = user.DisplayName, isAdmin = user.IsAdmin, allowedOperators = allowed, canSeeCosts, cliente = nomeCliente });
})
.WithName("GetMe");

// Orario previsto per giorno per ogni operatore (accessibile a tutti gli autenticati)
app.MapGet("/api/operators/schedule", (HttpContext ctx, BigQueryClient bq) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null) return Results.Unauthorized();

    try
    {
        const string sql =
            "SELECT Nome, Cognome, Ore_Lunedi, Ore_Martedi, Ore_Mercoledi, " +
            "Ore_Giovedi, Ore_Venerdi, Ore_Sabato, Ore_Domenica_Festivi " +
            "FROM `tymex-1a330.tymex_client.operators`";
        var result = bq.ExecuteQuery(sql, null);

        double ParseOre(BigQueryRow row, string field)
        {
            var v = row[field]?.ToString() ?? "";
            return double.TryParse(v.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        var schedule = new Dictionary<string, object>();
        foreach (var row in result)
        {
            var nome    = row["Nome"]?.ToString() ?? "";
            var cognome = row["Cognome"]?.ToString() ?? "";
            var cognomeBase = cognome.Contains(' ') ? cognome.Split(' ')[0] : cognome;
            var name = $"{nome} {cognomeBase}".Trim();
            if (string.IsNullOrEmpty(name)) continue;
            schedule[name] = new {
                mon = ParseOre(row, "Ore_Lunedi"),
                tue = ParseOre(row, "Ore_Martedi"),
                wed = ParseOre(row, "Ore_Mercoledi"),
                thu = ParseOre(row, "Ore_Giovedi"),
                fri = ParseOre(row, "Ore_Venerdi"),
                sat = ParseOre(row, "Ore_Sabato"),
                sun = ParseOre(row, "Ore_Domenica_Festivi")
            };
        }
        return Results.Ok(schedule);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
})
.WithName("GetOperatorSchedule");

// Costi orari operatori (solo admin)
app.MapGet("/api/operators/costs", async (HttpContext ctx, BigQueryClient bq) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null) return Results.Unauthorized();
    // Accessibile ad admin e a utenti con canSeeCosts
    var perm = userPermissions.GetValueOrDefault(user.Username);
    var canSee = user.IsAdmin || (perm?.CanSeeCosts ?? false);
    if (!canSee) return Results.Forbid();
    var ops = await GetOperatorsAsync(bq);
    // Chiave: "Nome Cognome" (come appare in operatorDisplayName dei documenti)
    // GroupBy per gestire duplicati (es. stesso nome con profilo ASSIST/MANAGER diverso): prende il costo più alto
    var costs = ops.Where(o => o.CostoOrario > 0)
                   .GroupBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                   .ToDictionary(g => g.Key, g => g.Max(o => o.CostoOrario), StringComparer.OrdinalIgnoreCase);
    return Results.Ok(costs);
})
.WithName("GetOperatorCosts");

// ========================================================
// ENDPOINT ADMIN UTENTI
// ========================================================

app.MapGet("/api/admin/users", (HttpContext ctx) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null || !user.IsAdmin) return Results.Forbid();
    return Results.Ok(appUsers.Select(u => new { u.Username, u.Password, u.DisplayName, u.IsAdmin }));
})
.WithName("GetUsers");

app.MapPost("/api/admin/users", async (HttpRequest request, HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null || !user.IsAdmin) return Results.Forbid();

    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body);
    var username    = json.RootElement.GetProperty("username").GetString()!;
    var password    = json.RootElement.GetProperty("password").GetString()!;
    var displayName = json.RootElement.GetProperty("displayName").GetString()!;
    var isAdmin     = json.RootElement.TryGetProperty("isAdmin", out var ia) && ia.GetBoolean();

    appUsers.RemoveAll(u => u.Username == username);
    appUsers.Add(new AppUser(username, password, displayName, isAdmin));
    _ = UpsertRailwayEnvVar("USERS_JSON", System.Text.Json.JsonSerializer.Serialize(appUsers), httpFactory);
    return Results.Ok(new { success = true });
})
.WithName("UpsertUser");

app.MapDelete("/api/admin/users/{username}", async (string username, HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null || !user.IsAdmin) return Results.Forbid();
    if (user.Username == username) return Results.BadRequest("Non puoi eliminare il tuo account");

    appUsers.RemoveAll(u => u.Username == username);
    userPermissions.Remove(username);
    _ = UpsertRailwayEnvVar("USERS_JSON", System.Text.Json.JsonSerializer.Serialize(appUsers), httpFactory);
    _ = UpsertRailwayEnvVar("PERMISSIONS_JSON", System.Text.Json.JsonSerializer.Serialize(userPermissions), httpFactory);
    return Results.Ok(new { success = true });
})
.WithName("DeleteUser");

// ========================================================
// ENDPOINT ADMIN PERMESSI
// ========================================================

app.MapGet("/api/admin/permissions", (HttpContext ctx) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null || !user.IsAdmin) return Results.Forbid();
    return Results.Ok(userPermissions);
})
.WithName("GetPermissions");

app.MapPost("/api/admin/permissions", async (HttpRequest request, HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null || !user.IsAdmin) return Results.Forbid();

    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body);
    var targetUsername = json.RootElement.GetProperty("username").GetString()!;

    List<string>? allowedOps = null;
    if (json.RootElement.TryGetProperty("allowedOperators", out var ops) && ops.ValueKind != System.Text.Json.JsonValueKind.Null)
        allowedOps = ops.EnumerateArray().Select(e => e.GetString()!).ToList();

    var canSeeCosts = json.RootElement.TryGetProperty("canSeeCosts", out var csc) && csc.GetBoolean();
    userPermissions[targetUsername] = new UserPermission(allowedOps, canSeeCosts);

    _ = UpsertRailwayEnvVar("PERMISSIONS_JSON", System.Text.Json.JsonSerializer.Serialize(userPermissions), httpFactory);
    return Results.Ok(new { success = true });
})
.WithName("UpdatePermissions");

// Salvataggio bulk: tutti i permessi in un unico POST → una sola chiamata Railway
app.MapPost("/api/admin/permissions/bulk", async (HttpRequest request, HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var user = ctx.Items["CurrentUser"] as AppUser;
    if (user == null || !user.IsAdmin) return Results.Forbid();

    var body = await new StreamReader(request.Body).ReadToEndAsync();
    var incoming = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, UserPermission>>(body);
    if (incoming == null) return Results.BadRequest();

    foreach (var kv in incoming)
        userPermissions[kv.Key] = kv.Value;

    var railwayOk = await UpsertRailwayEnvVar("PERMISSIONS_JSON", System.Text.Json.JsonSerializer.Serialize(userPermissions), httpFactory);
    return Results.Ok(new { success = true, persisted = railwayOk });
})
.WithName("BulkUpdatePermissions");

app.Run();

// ========================================================
// HELPERS RAILWAY
// ========================================================

// ========================================================
// OPERATORS BIGQUERY CACHE
// ========================================================
async Task<List<OperatorBQ>> GetOperatorsAsync(BigQueryClient bq)
{
    if (DateTime.Now < operatorsCacheExpiry) return operatorsCache;
    await operatorsCacheLock.WaitAsync();
    try
    {
        if (DateTime.Now < operatorsCacheExpiry) return operatorsCache;

        // Prova query con tutte le colonne; fallback progressivo per colonne opzionali
        // costoOrario = tariffa oraria diretta (campo preferito); CostoLavorazione = costo mensile (vecchio sistema)
        string[] queries = [
            "SELECT Cognome, Nome, IndirEmail, CodRisorsaProd, costoOrario, CostoLavorazione, password FROM `tymex-1a330.tymex_client.operators` WHERE IndirEmail IS NOT NULL",
            "SELECT Cognome, Nome, IndirEmail, CodRisorsaProd, costoOrario, CostoLavorazione, '' AS password FROM `tymex-1a330.tymex_client.operators` WHERE IndirEmail IS NOT NULL",
            "SELECT Cognome, Nome, IndirEmail, CodRisorsaProd, 0.0 AS costoOrario, CostoLavorazione, '' AS password FROM `tymex-1a330.tymex_client.operators` WHERE IndirEmail IS NOT NULL",
            "SELECT Cognome, Nome, IndirEmail, CodRisorsaProd, 0.0 AS costoOrario, '' AS CostoLavorazione, '' AS password FROM `tymex-1a330.tymex_client.operators` WHERE IndirEmail IS NOT NULL"
        ];

        Google.Cloud.BigQuery.V2.BigQueryResults? result = null;
        foreach (var sql in queries)
        {
            try { result = bq.ExecuteQuery(sql, null); break; }
            catch { }
        }
        if (result == null) return operatorsCache;

        double ParseCosto(string? s) => double.TryParse((s ?? "").Replace(',', '.'),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

        var ops = new List<OperatorBQ>();
        foreach (var row in result)
        {
            var nome     = row["Nome"]?.ToString() ?? "";
            var cognome  = row["Cognome"]?.ToString() ?? "";
            var email    = row["IndirEmail"]?.ToString() ?? "";
            var profilo  = row["CodRisorsaProd"]?.ToString() ?? "ASSIST"; // ADMIN / ASSIST / MANAGER
            var pwd      = row["password"]?.ToString() ?? "";
            // Usa costoOrario (tariffa oraria) se > 0, altrimenti CostoLavorazione come fallback
            var costoOrario    = ParseCosto(row["costoOrario"]?.ToString());
            var costoMensile   = ParseCosto(row["CostoLavorazione"]?.ToString());
            var costo = costoOrario > 0 ? costoOrario : costoMensile;
            // DisplayName: "Nome Cognome" per corrispondere a operatorDisplayName nei documenti
            // Se Cognome contiene il ruolo (es. "Loffi ASSIST"), usa solo la prima parola
            var cognomeBase = cognome.Contains(' ') ? cognome.Split(' ')[0] : cognome;
            var displayName = $"{nome} {cognomeBase}".Trim();
            if (!string.IsNullOrEmpty(email))
                ops.Add(new OperatorBQ(email, pwd, displayName, profilo, costo));
        }
        operatorsCache = ops;
        operatorsCacheExpiry = DateTime.Now.AddMinutes(5);
        return ops;
    }
    catch { return operatorsCache; }
    finally { operatorsCacheLock.Release(); }
}

async Task<bool> UpsertRailwayEnvVar(string name, string value, IHttpClientFactory httpFactory)
{
    var token     = Environment.GetEnvironmentVariable("RAILWAY_API_TOKEN");
    var projectId = Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID");
    var envId     = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT_ID");
    var serviceId = Environment.GetEnvironmentVariable("RAILWAY_SERVICE_ID");
    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(projectId) ||
        string.IsNullOrEmpty(envId) || string.IsNullOrEmpty(serviceId)) return false;

    var http = httpFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var mutation = System.Text.Json.JsonSerializer.Serialize(new
    {
        query = "mutation($input: VariableUpsertInput!) { variableUpsert(input: $input) }",
        variables = new { input = new { projectId, environmentId = envId, serviceId, name, value } }
    });

    var content = new StringContent(mutation, System.Text.Encoding.UTF8, "application/json");
    try
    {
        var resp = await http.PostAsync("https://backboard.railway.app/graphql/v2", content);
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}

// ========================================================
// TIPI
// ========================================================

record AppUser(
    [property: JsonPropertyName("username")]    string Username,
    [property: JsonPropertyName("password")]    string Password,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("isAdmin")]     bool   IsAdmin = false,
    double CostoOrario = 0,
    List<string>? ForcedAllowedOperators = null
);

record OperatorBQ(string Email, string Password, string DisplayName, string Profilo, double CostoOrario);

record UserPermission(
    [property: JsonPropertyName("allowedOperators")] List<string>? AllowedOperators = null,
    [property: JsonPropertyName("canSeeCosts")]       bool          CanSeeCosts      = false
);
