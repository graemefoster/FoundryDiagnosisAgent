---
name: foundry-low-level-network-diagnostics
description: Perform low-level TCP/DNS/route network diagnostics for Azure AI Foundry environments. Use when connectivity to endpoints has been confirmed as a potential issue and deeper packet/route-level investigation is needed. Trigger phrases include "trace route", "dns not resolving", "tcp connection refused", "packet loss", "network latency", "can't connect to endpoint", "TLS handshake failure", "proxy blocking", "firewall blocking traffic".
---

# Low-Level Network Diagnostics

Use this skill when a higher-level diagnostic has identified a potential network connectivity issue and deeper investigation at the TCP/DNS/route level is required.

## When to Use

- DNS resolution failures or unexpected answers
- TCP connection timeouts or resets to known endpoints
- TLS/proxy handshake issues
- Route path analysis needed (hop-by-hop)
- Latency or packet loss characterisation

## Environment Assumption

Assume ICMP may be blocked. Prefer TCP/HTTP-based path diagnostics over ping-based conclusions.

## Installed Tooling

System tools:
- `tcptraceroute` for TCP hop discovery to service ports.
- `traceroute -T` as a TCP traceroute alternative.
- `mtr --tcp` for repeated TCP path sampling.
- `nmap` for port state and latency characteristics.
- `curl` and `nc` for connectivity and TLS/proxy checks.
- `dnsutils` (`dig`, `nslookup`) for DNS diagnostics.
- `iproute2` (`ip route`, `ip addr`) for route/interface checks.
- `tcpdump` only when packet capture is explicitly requested and permitted.

Python libraries:
- Diagnostics: `scapy`, `dnspython`, `requests`, `httpx`, `python-nmap`, `netaddr`.
- Analysis/visuals: `pandas`, `numpy`, `matplotlib`, `seaborn`, `plotly`, `networkx`, `tabulate`, `rich`.

## Diagnostic Workflow (TCP-First)

1. Confirm target FQDN, port, protocol, and expected network path.
2. Validate DNS answers and private/public endpoint alignment.
3. Test socket reachability (`nc`, `curl`, `nmap`) on the target port.
4. Run route analysis with `tcptraceroute` or `mtr --tcp`.
5. Correlate findings to NSG/UDR/firewall/proxy/Private Link controls.
6. Propose least-disruptive fix, then verify with the same checks.

## Charting Guidance

When presenting route or latency diagnostics, prefer simple visuals:
- Hop vs RTT line chart.
- Per-hop loss or timeout heatmap.
- Side-by-side comparison chart for before/after remediation.

## Output Format

1. Target endpoint and port tested.
2. DNS resolution result (with any private zone detail).
3. TCP connectivity result (open/filtered/closed).
4. Route analysis summary (hops, latency, loss).
5. Root cause hypothesis with confidence level.
6. Recommended fix and verification command.
