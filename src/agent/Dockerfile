FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Install Python and network diagnostics tooling (TCP-first, ICMP-optional)
RUN apt-get update && apt-get install -y \
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
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Copy skills packages into the container (sourced from src/agent/skills/ in the build context)
COPY --from=build /src/skills ./skills

ENTRYPOINT ["dotnet", "FoundryDiagnosisAgent.Agent.dll"]
