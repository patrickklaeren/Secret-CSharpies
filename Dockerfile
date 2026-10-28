FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/SecretSanta/SecretSanta.csproj src/SecretSanta/
RUN dotnet restore src/SecretSanta/SecretSanta.csproj
COPY src/SecretSanta/ src/SecretSanta/
RUN dotnet publish src/SecretSanta/SecretSanta.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
RUN mkdir /db && chown app:app /db
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Database__Directory=/db
VOLUME /db
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "SecretSanta.dll"]
