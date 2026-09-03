# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copy riêng file project trước để tầng restore được cache lại giữa các lần đổi mã nguồn.
COPY global.json ./
COPY IncidentTracker.sln ./
COPY src/IncidentTracker.Api/IncidentTracker.Api.csproj src/IncidentTracker.Api/
COPY tests/IncidentTracker.Api.Tests/IncidentTracker.Api.Tests.csproj tests/IncidentTracker.Api.Tests/
RUN dotnet restore src/IncidentTracker.Api/IncidentTracker.Api.csproj

COPY . .
RUN dotnet publish src/IncidentTracker.Api/IncidentTracker.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# curl phục vụ healthcheck của docker compose (NFR-PORT-01).
# Ảnh aspnet:8.0-alpine đã có sẵn user không đặc quyền tên "app", chỉ cần dùng lại.
RUN apk add --no-cache curl

COPY --from=build --chown=app:app /app/publish ./
# Upload local (Architecture v3.1, BR-EV-05) khi không cấu hình S3; volume compose mount vào đây.
RUN mkdir -p /app/storage && chown app:app /app/storage
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

ENTRYPOINT ["dotnet", "IncidentTracker.Api.dll"]
