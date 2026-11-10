FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/src/UrlCrawler.Domain/UrlCrawler.Domain.csproj backend/src/UrlCrawler.Domain/
COPY backend/src/UrlCrawler.Infrastructure/UrlCrawler.Infrastructure.csproj backend/src/UrlCrawler.Infrastructure/
COPY backend/src/UrlCrawler.Api/UrlCrawler.Api.csproj backend/src/UrlCrawler.Api/
RUN dotnet restore backend/src/UrlCrawler.Api/UrlCrawler.Api.csproj
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/UrlCrawler.Api/UrlCrawler.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "UrlCrawler.Api.dll"]
