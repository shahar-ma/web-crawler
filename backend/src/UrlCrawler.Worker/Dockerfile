FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/src/UrlCrawler.Domain/UrlCrawler.Domain.csproj backend/src/UrlCrawler.Domain/
COPY backend/src/UrlCrawler.Infrastructure/UrlCrawler.Infrastructure.csproj backend/src/UrlCrawler.Infrastructure/
COPY backend/src/UrlCrawler.Worker/UrlCrawler.Worker.csproj backend/src/UrlCrawler.Worker/
RUN dotnet restore backend/src/UrlCrawler.Worker/UrlCrawler.Worker.csproj
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/UrlCrawler.Worker/UrlCrawler.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "UrlCrawler.Worker.dll"]
