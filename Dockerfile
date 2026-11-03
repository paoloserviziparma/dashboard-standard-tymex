# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY TymexDashboard.csproj .
RUN dotnet restore TymexDashboard.csproj
COPY . .
RUN dotnet publish TymexDashboard.csproj -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV PORT=8080
ENTRYPOINT ["dotnet", "TymexDashboard.dll"]
