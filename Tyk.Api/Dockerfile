FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TimeTracker.Api/TimeTracker.Api.csproj", "TimeTracker.Api/"]
COPY ["TimeTracker.Application/TimeTracker.Application.csproj", "TimeTracker.Application/"]
COPY ["TimeTracker.Domain/TimeTracker.Domain.csproj", "TimeTracker.Domain/"]
COPY ["TimeTracker.Infrastructure/TimeTracker.Infrastructure.csproj", "TimeTracker.Infrastructure/"]
RUN dotnet restore "TimeTracker.Api/TimeTracker.Api.csproj"
COPY . .
WORKDIR "/src/TimeTracker.Api"
RUN dotnet build "TimeTracker.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "TimeTracker.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TimeTracker.Api.dll"]
