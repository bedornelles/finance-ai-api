# Etapa 1: build
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

COPY RegistrAi.Api.csproj ./
RUN dotnet restore "RegistrAi.Api.csproj"

COPY . .
RUN dotnet publish "RegistrAi.Api.csproj" -c Release -o /app/publish

# Etapa 2: runtime (imagem final, mais leve)
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "RegistrAi.Api.dll"]