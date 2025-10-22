FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS base
ARG TARGETARCH
USER $APP_UID

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0+docker
WORKDIR /src
COPY ["CosmosJsonPatch.slnx", "."]
COPY ["CosmosJsonPatch/CosmosJsonPatch.csproj", "CosmosJsonPatch/"]
RUN dotnet restore
COPY . .
RUN dotnet build --no-restore -c $BUILD_CONFIGURATION -o /app/build /p:Version=${VERSION}

FROM build AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0+docker
RUN dotnet publish "CosmosJsonPatch/CosmosJsonPatch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:Version=${VERSION} -a $TARGETARCH --use-current-runtime --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish/CosmosJsonPatch .
ENTRYPOINT ["/app/CosmosJsonPatch"]