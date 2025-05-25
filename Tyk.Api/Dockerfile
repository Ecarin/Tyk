FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Tyk.Api/Tyk.Api.csproj", "Tyk.Api/"]
COPY ["Tyk.Application/Tyk.Application.csproj", "Tyk.Application/"]
COPY ["Tyk.Domain/Tyk.Domain.csproj", "Tyk.Domain/"]
COPY ["Tyk.Infrastructure/Tyk.Infrastructure.csproj", "Tyk.Infrastructure/"]
RUN dotnet restore "Tyk.Api/Tyk.Api.csproj"
COPY . .
WORKDIR "/src/Tyk.Api"
RUN dotnet build "Tyk.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Tyk.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Tyk.Api.dll"]
