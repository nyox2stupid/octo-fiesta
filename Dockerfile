# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

ARG VERSION=0.0.0-dev

COPY octo-fiesta.sln .
COPY octo-fiesta/octo-fiesta.csproj octo-fiesta/
COPY octo-fiesta.Tests/octo-fiesta.Tests.csproj octo-fiesta.Tests/

RUN dotnet restore

COPY octo-fiesta/ octo-fiesta/
COPY octo-fiesta.Tests/ octo-fiesta.Tests/

RUN dotnet publish octo-fiesta/octo-fiesta.csproj -c Release -p:Version=$VERSION -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# yt-dlp and ffmpeg power YouTube Music search, extraction, and audio conversion.
# Deno is installed as yt-dlp's JavaScript runtime for modern YouTube extraction.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl ffmpeg python3 python3-pip unzip \
    && pip3 install --break-system-packages --no-cache-dir "yt-dlp[default]" \
    && curl -fsSL https://deno.land/install.sh | DENO_INSTALL=/usr/local sh \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/downloads

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "octo-fiesta.dll"]
