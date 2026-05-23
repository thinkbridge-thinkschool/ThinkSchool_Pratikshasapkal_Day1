# Azure Container Apps — Deployment Guide

**Day 5 · Piece 3 — Cloud-Native Deployment of Quotes API**

---

## Table of Contents

1. [Overview](#1-overview)
2. [Azure Resource Group Creation](#2-azure-resource-group-creation)
3. [Container Apps Environment](#3-container-apps-environment)
4. [Azure Container Registry (ACR)](#4-azure-container-registry-acr)
5. [Docker Image Tagging and Push](#5-docker-image-tagging-and-push)
6. [Azure Container App Deployment](#6-azure-container-app-deployment)
7. [Public Endpoint Verification](#7-public-endpoint-verification)
8. [What I Learned](#8-what-i-learned)
9. [What Would Break This](#9-what-would-break-this)

---

## 1. Overview

**Azure Container Apps** is a fully managed serverless container platform built on top of Kubernetes and KEDA (Kubernetes Event-Driven Autoscaling). It lets you deploy containerised applications without managing infrastructure — no clusters to provision, no nodes to patch.

### Key concepts

| Concept | What it means |
|---|---|
| **Serverless containers** | Azure manages the underlying compute. You only define what your app needs (image, port, env vars, replicas). |
| **Autoscaling** | Replicas scale in and out automatically based on HTTP traffic, queue depth, or a custom KEDA rule. You define `--min-replicas` and `--max-replicas`. |
| **Revisions** | Every deployment creates an immutable revision. Traffic can be split between revisions for blue/green or canary releases. Old revisions remain available for instant rollback. |
| **Ingress** | Built-in HTTP/S routing. External ingress exposes the app to the public internet; internal ingress keeps it reachable only within the environment's virtual network. |
| **Observability** | Native integration with Azure Monitor and Application Insights. Container logs stream in real time via `az containerapp logs tail`. |

Azure Container Apps sits between App Service (opinionated, PaaS) and AKS (full control, complex). It is the right choice when you want container-level control without Kubernetes expertise.

---

## 2. Azure Resource Group Creation

A **Resource Group** is a logical container in Azure that holds related resources — it is not a compute or network resource itself. Everything deployed for this project (ACR, Container App, etc.) lives inside one resource group so it can be managed, monitored, and deleted together.

```bash
az group create -n thinkschool-rg -l centralindia
```

| Flag | Value | Purpose |
|---|---|---|
| `-n` | `thinkschool-rg` | Name of the resource group |
| `-l` | `centralindia` | Azure region — chose Central India for low latency |

> **Tip:** All subsequent resources should be created in the same region to avoid cross-region data transfer costs and latency.

---

## 3. Container Apps Environment

A **Container Apps Environment** is the shared boundary within which one or more Container Apps run. It provides:

- **Shared virtual networking** — apps in the same environment can call each other over internal DNS without going through the public internet.
- **Shared log destination** — all apps stream logs to a single Log Analytics workspace, making correlation across services easy.
- **Shared VNET integration** — custom VNET peering is configured once at the environment level, not per-app.

Think of the environment as the "cluster" and individual Container Apps as the "deployments" inside it.

### Subscription limitation encountered

Creating a new Container Apps Environment requires a dedicated Log Analytics workspace and subnet allocation. The Azure subscription used for this project had reached its environment quota for the `centralindia` region during the session.

**Resolution:** An existing shared environment was reused instead of creating a new one.

List available environments to find one to reuse:

```bash
az containerapp env list -o table
```

**Reused environment details:**

| Property | Value |
|---|---|
| Environment name | `cae-342m3golxdrt6` |
| Resource group | `rg-quotes-amey` |
| Region | `centralindia` |

> **Note:** When reusing an environment that belongs to a different resource group, the Container App itself must be deployed into that same resource group (the environment and the app must be co-located).

---

## 4. Azure Container Registry (ACR)

### Why local Docker images cannot be deployed directly

Azure Container Apps pulls images from a **container registry** at deploy time — it does not accept a local image tarball or a Docker daemon on your laptop. The registry must be reachable from Azure's network.

Options:
- **Azure Container Registry (ACR)** — private, integrated with Azure RBAC and managed identity.
- **Docker Hub** — public images only (or paid private repositories).
- **GitHub Container Registry (ghcr.io)** — common in CI/CD pipelines.

For production and for images that contain proprietary code, ACR is the correct choice. It keeps images private, supports geo-replication, and integrates natively with Container Apps for credential-free pulls via managed identity.

### Create the registry

```bash
az acr create -n thinkschoolacr123 -g thinkschool-rg --sku Basic
```

| Flag | Value | Purpose |
|---|---|---|
| `-n` | `thinkschoolacr123` | Registry name — must be globally unique across all of Azure |
| `-g` | `thinkschool-rg` | Resource group that owns the registry |
| `--sku` | `Basic` | Cheapest tier; suitable for development and low-volume production |

ACR SKU comparison:

| SKU | Storage | Webhooks | Geo-replication | Use case |
|---|---|---|---|---|
| Basic | 10 GB | No | No | Dev / learning |
| Standard | 100 GB | Yes | No | Most production |
| Premium | 500 GB | Yes | Yes | Enterprise / global |

### Authenticate Docker to the registry

```bash
az acr login -n thinkschoolacr123
```

This command calls the ACR OAuth endpoint using your current Azure CLI session and writes a short-lived credential into your local Docker config (`~/.docker/config.json`). After this, `docker push` and `docker pull` commands for `thinkschoolacr123.azurecr.io` are authenticated automatically.

---

## 5. Docker Image Tagging and Push

### Why tagging is necessary

Docker images are identified by a **registry/repository:tag** triplet. The image `quotesapi:latest` exists only in the local Docker daemon. To push it to ACR it must be given a fully qualified name that includes the ACR hostname.

### Tag the image

```bash
docker tag quotesapi:latest thinkschoolacr123.azurecr.io/quotesapi:0.1.0
```

This does **not** copy or re-build the image — it adds a new name that points to the same image layers. Both names (`quotesapi:latest` and `thinkschoolacr123.azurecr.io/quotesapi:0.1.0`) now refer to the same content locally.

Naming breakdown:

```
thinkschoolacr123.azurecr.io  /  quotesapi  :  0.1.0
└─── ACR hostname ───────────┘  └─ repo ──┘  └─ tag ─┘
```

Using an explicit version tag (`0.1.0`) rather than `latest` is a production best practice — it makes every deployment reproducible and auditable.

### Push the image to ACR

```bash
docker push thinkschoolacr123.azurecr.io/quotesapi:0.1.0
```

Docker uploads only the layers that ACR does not already have (layer deduplication). On subsequent pushes of the same image with only application-code changes, only the top layer is transferred — base image layers are already cached in the registry.

---

## 6. Azure Container App Deployment

### Full deployment command

```bash
az containerapp create ^
  -n pratiksha-quotes-api ^
  -g rg-quotes-amey ^
  --environment cae-342m3golxdrt6 ^
  --image thinkschoolacr123.azurecr.io/quotesapi:0.1.0 ^
  --target-port 8080 ^
  --ingress external ^
  --registry-server thinkschoolacr123.azurecr.io ^
  --min-replicas 1 ^
  --max-replicas 2 ^
  --env-vars Jwt__Key=super-secret-development-key-12345
```

> The `^` character is the Windows Command Prompt line-continuation character. Use `\` on Linux/macOS or PowerShell's backtick `` ` ``.

### Flag-by-flag explanation

#### `-n pratiksha-quotes-api`
The name of the Container App. This becomes part of the public URL and must be unique within the environment.

#### `-g rg-quotes-amey`
The resource group that the Container App is deployed into. Must match the resource group of the reused environment (`cae-342m3golxdrt6`).

#### `--environment cae-342m3golxdrt6`
The Container Apps Environment that provides networking and logging for this app.

#### `--image thinkschoolacr123.azurecr.io/quotesapi:0.1.0`
The fully qualified image reference. Azure pulls this image from ACR when starting each replica.

#### `--target-port 8080`
The port your application listens on **inside the container**. Must match `ASPNETCORE_HTTP_PORTS=8080` or the Kestrel binding configured in the app. Azure Container Apps handles TLS termination externally — your container only needs to speak plain HTTP on this port.

#### `--ingress external`
Makes the app reachable from the public internet. Azure provisions an HTTPS endpoint automatically and handles TLS certificates. The alternative is `internal`, which restricts access to other apps within the same environment.

| Ingress mode | Reachable from |
|---|---|
| `external` | Public internet (and internal) |
| `internal` | Only apps within the same environment |
| _(omitted)_ | No HTTP ingress — background worker / job |

#### `--registry-server thinkschoolacr123.azurecr.io`
Tells Container Apps which registry to authenticate against when pulling the image. Azure uses the current CLI session's credentials to grant the Container App access to ACR.

#### `--min-replicas 1` and `--max-replicas 2`
Defines the autoscaling envelope:
- `--min-replicas 1` — at least one instance is always running (no cold starts).
- `--max-replicas 2` — Azure will spin up a second replica automatically under load and scale back down when traffic drops.

Setting `--min-replicas 0` enables true serverless behaviour (scales to zero when idle) but introduces a cold-start delay on the first request after a period of inactivity.

#### `--env-vars Jwt__Key=super-secret-development-key-12345`
Injects environment variables into every container replica at startup. ASP.NET Core reads these using the `__` double-underscore convention to represent nested JSON keys:

```
Jwt__Key  →  appsettings.json { "Jwt": { "Key": "..." } }
```

> **Security note:** For production, use Azure Key Vault references (`secretref:`) or managed identity to avoid storing secret values in plain text inside the Container App configuration.

#### Revisions

Every `az containerapp update` (or re-deployment with a new image tag) creates a new **revision** — an immutable snapshot of the container configuration. By default, 100% of traffic is routed to the latest revision. You can split traffic between revisions using:

```bash
az containerapp ingress traffic set \
  -n pratiksha-quotes-api \
  -g rg-quotes-amey \
  --revision-weight latest=80 previous=20
```

This enables canary deployments and instant rollback without downtime.

---

## 7. Public Endpoint Verification

After deployment, Azure Container Apps assigned the following public HTTPS URL:

```
https://pratiksha-quotes-api.livelydune-368712a9.centralindia.azurecontainerapps.io/
```

### Endpoints verified

| Endpoint | Method | Expected response | Result |
|---|---|---|---|
| `/` | GET | `"Quotes API Running"` | ✅ 200 OK |
| `/health` | GET | `{"status":"healthy"}` | ✅ 200 OK |

### How to verify from the terminal

```bash
# Root endpoint
curl https://pratiksha-quotes-api.livelydune-368712a9.centralindia.azurecontainerapps.io/

# Health check
curl https://pratiksha-quotes-api.livelydune-368712a9.centralindia.azurecontainerapps.io/health

# Stream live container logs
az containerapp logs tail \
  -n pratiksha-quotes-api \
  -g rg-quotes-amey
```

### URL structure explained

```
https://pratiksha-quotes-api.livelydune-368712a9.centralindia.azurecontainerapps.io
         └─── app name ──────┘└── environment ID ───┘└── region ─┘
```

The domain is automatically provisioned and backed by a Microsoft-managed TLS certificate. No manual certificate creation or DNS configuration was required.

---

## 8. What I Learned

### Cloud-native deployments are declarative
Instead of SSHing into a server and running commands, you declare the desired state (image, replicas, env vars, port) and the platform converges to that state. This is a fundamentally different mental model from traditional server management.

### The ACR and Container Apps relationship
Container Apps is a **consumer** of images; ACR is a **producer**. They are deliberately decoupled: you can push a new image to ACR without redeploying, and you can point a Container App at any accessible registry. The deployment step is the act of connecting them — telling Azure which image and which registry to use.

### Container runtime configuration belongs in environment variables
The JWT key, connection strings, and feature flags are not in the image — they are injected at deploy time via `--env-vars`. This means the same image can be deployed to development, staging, and production with different configuration and secrets, without rebuilding.

### Why ASPNETCORE_HTTP_PORTS matters
The aspnet base image sets `ASPNETCORE_HTTP_PORTS=8080` by default. Kestrel binds to `0.0.0.0:8080` inside the container, and `--target-port 8080` tells Container Apps where to forward traffic. If these two values disagree, the load balancer sends traffic to a port nothing is listening on and health probes fail.

### Revisions make deployments safe
Every deployment is non-destructive. The previous revision stays running until traffic is explicitly moved away from it. This removes the risk of a bad deployment causing downtime — roll back in seconds by shifting traffic back to the previous revision.

---

## 9. What Would Break This

Understanding failure modes is as important as knowing the happy path.

### Missing or wrong environment variables

| Variable | What breaks if it is missing |
|---|---|
| `Jwt__Key` | App fails `ValidateOnStart()` — crashes before accepting any requests |
| `ASPNETCORE_HTTP_PORTS` | Kestrel binds to wrong port; health probe fails; Container Apps marks app unhealthy and cycles replicas |
| `ConnectionStrings__Default` | SQLite uses `/tmp` path (acceptable) but data is lost on restart — or a wrong path causes Error 14 |

### Wrong target port

If `--target-port` does not match the port Kestrel is listening on, the Container Apps load balancer sends traffic to a closed port. The health probe returns a connection refused error, Container Apps marks the revision unhealthy, and no traffic is served.

### Registry authentication problems

If the Container App cannot pull the image from ACR (wrong registry server, expired credentials, missing ACR role assignment), the replica fails to start with an `ImagePullBackOff`-equivalent error. Symptoms: revision shows "Running" but `0/1` replicas are active.

Common causes:
- Typo in `--registry-server`
- The identity used by Container Apps was not granted `AcrPull` role on the ACR resource
- The image tag specified in `--image` does not exist in the registry

### Container startup failures

If the application process exits during startup (unhandled exception, failed `ValidateOnStart`, migration error), Container Apps will restart the replica up to a threshold and then mark the revision `Failed`. Always check logs first:

```bash
az containerapp logs tail -n pratiksha-quotes-api -g rg-quotes-amey
```

### Azure subscription quota limits

Azure Container Apps Environments are subject to per-region, per-subscription quotas. During this session the environment quota for `centralindia` was reached, which is why an existing environment was reused. If you hit this limit:

- Request a quota increase via the Azure portal (Subscriptions → Usage + Quotas).
- Or reuse an existing environment in the same region, as done here.

---

*Deployed as part of Day 5 · Piece 3 — Cloud-Native Container Deployment*
*Project: ThinkSchool .NET 10 Learning Path*
