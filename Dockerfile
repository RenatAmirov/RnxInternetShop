FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RnxInternetShop.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /home/app/.aspnet
COPY --from=build /app ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "RnxInternetShop.dll"]
