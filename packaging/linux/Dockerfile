# Linux build of the site server on Ubuntu 26.04 with the .NET 10 SDK of the
# distribution: the program with the site beside it, the tests, an archive and
# an image to run it in. The context is the root of the project, the site is a
# second context:
#
#   docker build -f packaging/linux/Dockerfile --build-context site=../CalendarWeb/site \
#       --target dist --output type=local,dest=<folder> .
#   docker build -f packaging/linux/Dockerfile --build-context site=../CalendarWeb/site \
#       --target runtime -t calendar-web .
#
# dist holds the archive and the report of the tests, and it is made only
# after the tests have passed. The test with a real server is skipped here:
# a build step sees no other container. build-linux.ps1 of the CosmosCalendar
# umbrella runs the whole set once more against the server image.

ARG UBUNTU=26.04

# ---- the SDK from the archive of the distribution ---------------------------
FROM ubuntu:${UBUNTU} AS sdk
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        dotnet-sdk-10.0 ca-certificates \
 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false

# ---- build ------------------------------------------------------------------
# The packages first: their layer stays while only the code changes.
FROM sdk AS build
WORKDIR /src
COPY CalendarWebServer.sln ./
COPY src/CalendarWebServer/CalendarWebServer.csproj src/CalendarWebServer/
COPY tests/CalendarWebServer.Tests/CalendarWebServer.Tests.csproj tests/CalendarWebServer.Tests/
RUN dotnet restore CalendarWebServer.sln
COPY . .
RUN dotnet build CalendarWebServer.sln -c Release --no-restore

# ---- tests: a failed one stops the build, and nothing is exported ----------
FROM build AS test
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
RUN mkdir /reports \
 && dotnet test CalendarWebServer.sln -c Release --no-build \
        --logger "console;verbosity=normal" | tee /reports/dotnet-test.txt

# ---- the program and the site, the archive ----------------------------------------
FROM build AS package
COPY --from=test /reports /dist/tests
RUN dotnet publish src/CalendarWebServer/CalendarWebServer.csproj -c Release --no-restore -o /app
COPY --from=site / /app/site/
RUN set -e; \
    version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/CalendarWebServer/CalendarWebServer.csproj); \
    name=CalendarWebServer-$version-linux-x64; \
    mkdir -p /stage; \
    cp -a /app /stage/$name; \
    sed 's/\r$//' packaging/linux/INSTALL.txt > /stage/$name/INSTALL.txt; \
    tar -C /stage --owner=0 --group=0 --numeric-owner -czf /dist/$name.tar.gz $name

FROM scratch AS dist
COPY --from=package /dist/ /

# ---- the image of the site server -------------------------------------------------
# Not root, the port of the site, the data folder (legal.json and the offer of
# the owner) in the volume /data; the sync server is given by --upstream.
FROM ubuntu:${UBUNTU} AS runtime
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        aspnetcore-runtime-10.0 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --home-dir /data --no-create-home web \
 && install -d -o web -g web /data
COPY --from=package /app /opt/calendar-web
USER web
VOLUME /data
EXPOSE 8780
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENTRYPOINT ["dotnet", "/opt/calendar-web/CalendarWebServer.dll", "--data-dir", "/data"]
