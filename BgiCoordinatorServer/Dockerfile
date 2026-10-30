FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["BgiCoordinatorServer.csproj", "."]
RUN dotnet restore
COPY . .
ARG SOURCE_REVISION
RUN dotnet publish -c Release -o /app/publish -p:SourceRevisionId="$SOURCE_REVISION"

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BgiCoordinatorServer.dll"]
