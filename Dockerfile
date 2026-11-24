# One image for every role: the orchestrator and each participant are the same binary with a different Role.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/TripBooking.Api/TripBooking.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Trips="Data Source=/app/data/trips.db" \
    ConnectionStrings__Flights="Data Source=/app/data/flights.db" \
    ConnectionStrings__Hotels="Data Source=/app/data/hotels.db" \
    ConnectionStrings__Cars="Data Source=/app/data/cars.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "TripBooking.Api.dll"]
