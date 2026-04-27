FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем и восстанавливаем зависимости
COPY *.csproj .
RUN dotnet restore

# Копируем весь код и собираем
COPY . .
RUN dotnet publish -c Release -o /app

# Финальный образ
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

# Копируем собранное приложение и данные
COPY --from=build /app .
COPY Data ./Data
COPY Images ./Images
COPY Messages ./Messages

# Точка входа
ENTRYPOINT ["dotnet", "CardsTelegramBotPirozki.dll"]
