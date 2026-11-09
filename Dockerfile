# edzsplit — Ubuntu Server 24.04 native image
# Runtime needs only the .NET 8 runtime and p7zip (no Windows, no EPLAN).

FROM ubuntu:24.04 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
        dotnet-sdk-8.0 p7zip-full \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY src/ ./src/
RUN dotnet publish src/edzsplit/edzsplit.csproj -c Release -r linux-x64 --self-contained false -o /app

FROM ubuntu:24.04 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
        dotnet-runtime-8.0 p7zip-full ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --uid 10001 edzsplit
USER edzsplit
WORKDIR /data
COPY --from=build /app /opt/edzsplit
ENV PATH="/opt/edzsplit:${PATH}"
ENTRYPOINT ["edzsplit"]
CMD ["--help"]
