# syntax=docker/dockerfile:1

# One Dockerfile for every .NET service.
#   app:      the API or a worker, chosen with --build-arg PROJECT=<project name>
#   migrator: an EF Core migrations bundle that applies pending migrations, then exits
# docker-compose.yml builds each service from this file.

ARG DOTNET_VERSION=9.0

# ---------------------------------------------------------------- restore
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src

# Project files first, so the restore layer stays cached until a csproj changes.
COPY src/WaxyCandles.Domain/WaxyCandles.Domain.csproj src/WaxyCandles.Domain/
COPY src/WaxyCandles.Application/WaxyCandles.Application.csproj src/WaxyCandles.Application/
COPY src/WaxyCandles.Infrastructure/WaxyCandles.Infrastructure.csproj src/WaxyCandles.Infrastructure/
COPY src/WaxyCandles.Api/WaxyCandles.Api.csproj src/WaxyCandles.Api/
COPY src/WaxyCandles.PricePollingWorker/WaxyCandles.PricePollingWorker.csproj src/WaxyCandles.PricePollingWorker/
COPY src/WaxyCandles.OutboxWorker/WaxyCandles.OutboxWorker.csproj src/WaxyCandles.OutboxWorker/
COPY src/WaxyCandles.AlertWorker/WaxyCandles.AlertWorker.csproj src/WaxyCandles.AlertWorker/

RUN for project in src/*/*.csproj; do dotnet restore "$project"; done

COPY src/ src/

# ---------------------------------------------------------------- app
FROM restore AS app-build
ARG PROJECT
RUN test -n "$PROJECT" || (echo "Pass --build-arg PROJECT=<project name>" && exit 1)
RUN dotnet publish "src/$PROJECT/$PROJECT.csproj" \
        --configuration Release \
        --no-restore \
        --output /app

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS app
ARG PROJECT
WORKDIR /app
COPY --from=app-build /app .
ENV APP_DLL=${PROJECT}.dll
# Non-root user built into the official .NET images.
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]

# ---------------------------------------------------------------- migrator
FROM restore AS migrator-build
# Must match the EF Core major version the projects reference.
RUN dotnet tool install --global dotnet-ef --version 9.0.*
ENV PATH="${PATH}:/root/.dotnet/tools"

# Building the bundle starts the API's host to find the DbContext, and that
# host requires a JWT signing key. This placeholder exists only for this one
# command; it is not stored in the image and is never used to sign anything.
RUN Jwt__Secret=design-time-placeholder-not-a-real-secret \
    dotnet ef migrations bundle \
        --project src/WaxyCandles.Infrastructure \
        --startup-project src/WaxyCandles.Api \
        --configuration Release \
        --target-runtime linux-x64 \
        --output /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS migrator
WORKDIR /migrator
COPY --from=migrator-build /out/efbundle .
USER $APP_UID
# The connection string comes from ConnectionStrings__DefaultConnection.
ENTRYPOINT ["./efbundle"]
