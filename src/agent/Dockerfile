FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Install Python and network diagnostics tooling (TCP-first, ICMP-optional)
RUN apt-get update && apt-get install -y \
    python-is-python3 \
    python3 \
    python3-pip \
    ca-certificates \
    curl \
    dnsutils \
    iproute2 \
    iputils-ping \
    mtr-tiny \
    net-tools \
    netcat-openbsd \
    nmap \
    tcpdump \
    tcptraceroute \
    traceroute \
    && rm -rf /var/lib/apt/lists/*

# Install Python libraries for diagnostics, analysis, and chart output
RUN python3 -m pip install --no-cache-dir --break-system-packages \
    dnspython \
    httpx \
    matplotlib \
    netaddr \
    networkx \
    numpy \
    pandas \
    plotly \
    python-nmap \
    requests \
    rich \
    scapy \
    seaborn \
    tabulate

WORKDIR /app
EXPOSE 8088
ENV ASPNETCORE_URLS=http://+:8088
ENV PYTHONPATH=/app/skills

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG AGENT_BUILD_VERSION=0.0.0-local
ARG AGENT_GIT_SHA=unknown
WORKDIR /src

# Copy only the project file first so NuGet restore is cached in its own layer.
# This layer only invalidates when package references change.
COPY *.csproj .
RUN dotnet restore

# Trigger Copilot CLI download (~80MB) in a cached layer.
# Uses a stub source file to run the build target that fetches the CLI binary.
RUN echo 'var b = WebApplication.CreateBuilder(); b.Build().Run();' > Program.cs && \
    dotnet build -c Release -clp:ErrorsOnly 2>/dev/null || true && \
    rm Program.cs

# Now copy everything else and publish
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore \
    /p:Version=$AGENT_BUILD_VERSION \
    /p:InformationalVersion=$AGENT_BUILD_VERSION+$AGENT_GIT_SHA

FROM base AS final
ARG AGENT_BUILD_VERSION=0.0.0-local
ARG AGENT_GIT_SHA=unknown
WORKDIR /app
COPY --from=build /app/publish .
ENV DIAGNOSTICS_AGENT_BUILD_VERSION=$AGENT_BUILD_VERSION
ENV DIAGNOSTICS_AGENT_GIT_SHA=$AGENT_GIT_SHA

# Copy skills packages into the container (sourced from src/agent/skills/ in the build context)
COPY --from=build /src/skills ./skills

ENTRYPOINT ["dotnet", "FoundryDiagnosisAgent.Agent.dll"]
