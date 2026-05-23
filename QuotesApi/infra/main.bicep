// ═════════════════════════════════════════════════════════════════════════════
// main.bicep — Quotes API infrastructure
//
// Provisions all Azure resources required to run the Quotes API on
// Azure Container Apps. Called by `azd provision` (and `azd up`).
//
// Resource graph:
//
//   Log Analytics Workspace
//       └── Container Apps Environment
//               └── Container App  ←─ pulls image ─── Azure Container Registry
//                       └── User-Assigned Managed Identity
//                               └── AcrPull role  (runtime image pull)
//
//   Deploying user ──────────────────────────────── AcrPush role (azd image push)
//
// Deployment flow after `azd up`:
//   1. This Bicep runs  → all resources created/updated
//   2. azd builds image → `dotnet publish /t:PublishContainer`
//   3. azd pushes image → ACR (using deployer's AcrPush role)
//   4. azd updates app  → Container App revision points to new image tag
//                          (Container Apps pulls using managed identity AcrPull)
// ═════════════════════════════════════════════════════════════════════════════

targetScope = 'resourceGroup'


// ─────────────────────────────────────────────────────────────────────────────
// Parameters — injected by azd or set by the developer
// ─────────────────────────────────────────────────────────────────────────────

@minLength(1)
@maxLength(64)
@description('''
  Name of the azd environment (e.g. dev, staging, prod).
  Set during `azd init` or with: azd env new <name>
  Drives resource naming and the azd-env-name tag.
''')
param environmentName string

@minLength(1)
@description('''
  Azure region for all resources.
  Set with: azd env set AZURE_LOCATION centralindia
  Defaults to the resource group location if not overridden.
''')
param location string = resourceGroup().location

@description('''
  Object ID of the principal running `azd up` (developer or CI service principal).
  Injected automatically by azd as AZURE_PRINCIPAL_ID.
  Used to grant AcrPush so azd can push the built image during `azd deploy`.
''')
param principalId string = ''

@secure()
@description('''
  JWT signing key — injected into the Container App as an encrypted secret.
  Never stored in plain text.

  Set with:  azd env set JWT_KEY "your-secret-key-at-least-32-chars"
  Must be at least 32 characters to satisfy HMAC-SHA256 requirements.

  If left empty, the Container App starts but JWT-protected endpoints
  will reject all tokens (JwtOptions.ValidateOnStart may also crash the app).
''')
param jwtKey string = ''


// ─────────────────────────────────────────────────────────────────────────────
// Variables
// ─────────────────────────────────────────────────────────────────────────────

// Tags applied to every resource.
// azd-env-name lets you filter all resources for one environment in the portal
// and is used by `azd down` to identify what to delete.
var tags = {
  'azd-env-name': environmentName
  project: 'quotes-api'
  'managed-by': 'azd'
}

// Deterministic 13-character suffix derived from the resource group ID + env name.
// Guarantees globally unique names for ACR and Container Apps without manual input.
// Same inputs always produce the same suffix — safe to run `azd up` repeatedly.
var resourceToken = toLower(uniqueString(resourceGroup().id, environmentName))

// Resource names — follow Azure naming convention recommendations.
// Docs: https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming
var logAnalyticsName     = 'law-quotes-${resourceToken}'       // Log Analytics Workspace
var containerAppsEnvName = 'cae-quotes-${resourceToken}'       // Container Apps Environment
var registryName         = 'acrquotes${resourceToken}'         // ACR: alphanumeric only, max 50 chars
var identityName         = 'id-quotes-${resourceToken}'        // User-Assigned Managed Identity
var containerAppName     = 'quotes-api'                        // Human-readable; unique within environment


// ─────────────────────────────────────────────────────────────────────────────
// Built-in role definition IDs — stable across all Azure subscriptions
// Source: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles
// ─────────────────────────────────────────────────────────────────────────────
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'   // AcrPull
var acrPushRoleId = '8311e382-0749-4cb8-b61a-304f252e45ec'   // AcrPush


// ─────────────────────────────────────────────────────────────────────────────
// Conditional env vars and secrets
// Build the env var and secret lists here so the Container App resource stays
// readable. Bicep variables are evaluated at deploy time, not at runtime.
// ─────────────────────────────────────────────────────────────────────────────

// Secrets: only add the JWT secret entry if a key was actually provided
var containerSecrets = !empty(jwtKey) ? [
  {
    name: 'jwt-key'
    value: jwtKey
    // Stored encrypted by Azure Container Apps; never visible in portal or CLI output
  }
] : []

// Base environment variables always present in every replica
var baseEnvVars = [
  {
    // Tells Kestrel to listen on 0.0.0.0:8080 — matches targetPort below.
    // The aspnet base image already sets this, but declaring it explicitly
    // makes the intent visible in the Container App configuration.
    name: 'ASPNETCORE_HTTP_PORTS'
    value: '8080'
  }
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    // Program.cs checks this env var to choose /tmp/quotesapi-data as the
    // SQLite directory — always writable regardless of the container user.
    // The aspnet base image already sets this to "true"; declared here for
    // documentation and to guarantee the value even on custom base images.
    name: 'DOTNET_RUNNING_IN_CONTAINER'
    value: 'true'
  }
]

// JWT env var: references the secret by name — value is never echoed in plain text
var jwtEnvVar = !empty(jwtKey) ? [
  {
    name: 'Jwt__Key'
    secretRef: 'jwt-key'   // ASP.NET Core reads Jwt__Key → appsettings Jwt:Key
  }
] : []

var allEnvVars = concat(baseEnvVars, jwtEnvVar)


// ═════════════════════════════════════════════════════════════════════════════
// Resources
// ═════════════════════════════════════════════════════════════════════════════


// ─────────────────────────────────────────────────────────────────────────────
// User-Assigned Managed Identity
//
// A managed identity gives the Container App a cloud-native credential for
// calling Azure services (ACR image pull, Key Vault secret reads) without
// storing passwords or connection strings.
//
// Why user-assigned (not system-assigned):
//   System-assigned identities are tied to the resource lifecycle — they are
//   created when the Container App is created. We must assign the AcrPull role
//   BEFORE the Container App's first replica starts pulling images, which
//   requires the identity to exist first. User-assigned identities are
//   independent resources: create them, assign roles, then reference them in
//   the Container App — all in one Bicep deployment without circular dependency.
// ─────────────────────────────────────────────────────────────────────────────
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}


// ─────────────────────────────────────────────────────────────────────────────
// Log Analytics Workspace
//
// Container Apps Environment requires a Log Analytics workspace to collect
// container stdout/stderr and system logs. Without it, the environment cannot
// be created. The same workspace also powers Azure Monitor queries and alerts.
// ─────────────────────────────────────────────────────────────────────────────
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'   // Pay-per-GB ingestion — cheapest tier for development
    }
    retentionInDays: 30   // Keep 30 days of logs; increase for production auditing
  }
}


// ─────────────────────────────────────────────────────────────────────────────
// Container Apps Environment
//
// The environment is the shared boundary for all Container Apps within it.
// It provides:
//   • Virtual network — apps call each other via internal DNS, not public internet
//   • Log routing   — all container logs go to the Log Analytics workspace above
//   • KEDA          — Kubernetes Event-Driven Autoscaling engine (HTTP, queue, etc.)
//
// Quota note:
//   Azure limits the number of Container Apps Environments per subscription per
//   region. If provisioning fails with a quota error, either:
//     a) Request a quota increase: Azure portal → Subscriptions → Usage + Quotas
//     b) Reuse the shared environment from the manual deployment:
//        Change containerAppsEnvName to 'cae-342m3golxdrt6' and deploy the
//        Container App into resource group 'rg-quotes-amey'.
// ─────────────────────────────────────────────────────────────────────────────
resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: 'cae-342m3golxdrt6'
  scope: resourceGroup('rg-quotes-amey')
}


// ─────────────────────────────────────────────────────────────────────────────
// Azure Container Registry (ACR)
//
// ACR stores the container images that Container Apps pulls at runtime.
//
// Workflow:
//   BUILD   → `dotnet publish /t:PublishContainer` (developer machine / CI)
//   PUSH    → azd pushes image to ACR (uses deployer's AcrPush role below)
//   PULL    → Container App pulls image on startup/scaling (uses AcrPull below)
//
// adminUserEnabled: false — forces use of RBAC/managed identity instead of a
// shared admin password. Admin credentials are a single point of failure and
// cannot be scoped to specific registries or operations.
// ─────────────────────────────────────────────────────────────────────────────
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
    // Basic: 10 GB storage, no geo-replication, no webhooks — sufficient for dev/staging.
    // Upgrade to Standard (webhooks) or Premium (geo-replication) for production.
  }
  properties: {
    adminUserEnabled: false
  }
}


// ─────────────────────────────────────────────────────────────────────────────
// RBAC — AcrPull (managed identity → ACR)
//
// Grants the Container App's managed identity permission to pull images from ACR.
// This is how Azure Container Apps authenticates to the registry at runtime
// instead of using a username/password stored in the Container App configuration.
//
// Without this role: replica startup fails with "unauthorized: authentication required".
// ─────────────────────────────────────────────────────────────────────────────
resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // guid() generates a deterministic UUID — safe to redeploy without creating duplicates
  name: guid(registry.id, identity.id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}


// ─────────────────────────────────────────────────────────────────────────────
// RBAC — AcrPush (deploying user/service principal → ACR)
//
// Grants the developer or CI principal permission to push the built image to ACR
// during `azd deploy`. azd calls `docker push` on behalf of the deployer.
//
// Without this role: `azd deploy` fails with "denied: requested access to the
// resource is denied" when pushing the image.
//
// Skipped if principalId is not provided (e.g. in automated pipelines that use
// a service principal with its own role assignments).
// ─────────────────────────────────────────────────────────────────────────────
resource acrPushAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(registry.id, principalId, acrPushRoleId)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPushRoleId)
    principalId: principalId
    principalType: 'User'
  }
}


// ─────────────────────────────────────────────────────────────────────────────
// Container App — Quotes API
//
// The running application. Key points:
//
//   azd-service-name tag:
//     MUST match the service key in azure.yaml ('quotes-api').
//     azd uses this tag to discover which Container App belongs to which service
//     and sends the new image reference here after `azd deploy`.
//
//   Placeholder image:
//     The initial Bicep deployment uses a public Microsoft sample image so that
//     the Container App resource is created successfully. azd replaces this with
//     the real application image on the first (and every subsequent) `azd deploy`.
//
//   Managed identity + registry:
//     The userAssignedIdentity is attached and the registry block tells Container
//     Apps to use that identity when authenticating to ACR — no passwords stored.
//
//   Revisions:
//     Every `azd deploy` creates a new immutable revision. Traffic switches to
//     the latest revision automatically. Previous revisions stay available for
//     instant rollback:
//       az containerapp revision list -n quotes-api -g <rg>
//       az containerapp ingress traffic set -n quotes-api -g <rg> --revision-weight <old>=100
// ─────────────────────────────────────────────────────────────────────────────
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  // 'azd-service-name' MUST match the service key defined in azure.yaml
  tags: union(tags, { 'azd-service-name': 'quotes-api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      ingress: {
        external: true           // Expose to the public internet with an Azure-managed TLS cert
        targetPort: 8080         // Must match ASPNETCORE_HTTP_PORTS inside the container
        transport: 'auto'        // Negotiate HTTP/2; fall back to HTTP/1.1
        allowInsecure: false     // Redirect HTTP → HTTPS
      }
      // Pull images using the managed identity — no password stored in configuration
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      // Secrets are encrypted at rest by Azure Container Apps.
      // Referenced by 'secretRef' in env vars — values never appear in plain text
      // in the Azure portal, `az containerapp show`, or azd output.
      secrets: containerSecrets
    }
    template: {
      containers: [
        {
          name: 'quotes-api'
          // Placeholder — azd replaces this with the real image on `azd deploy`
          // mcr.microsoft.com/dotnet/samples:aspnetapp is publicly pullable,
          // so the Container App resource is created successfully in this Bicep run.
          image: 'mcr.microsoft.com/dotnet/samples:aspnetapp'
          resources: {
            cpu: json('0.25')   // 0.25 vCPU — sufficient for a lightweight REST API
            memory: '0.5Gi'     // 512 MiB
          }
          env: allEnvVars
        }
      ]
      scale: {
        minReplicas: 1     // Always keep one replica alive — no cold starts on first request
        maxReplicas: 3     // Scale out under load; Container Apps scales back down when idle
        rules: [
          {
            // KEDA HTTP scaling rule: add a replica for every 30 concurrent HTTP requests.
            // Tune concurrentRequests based on your app's observed throughput per replica.
            name: 'http-scaling'
            http: {
              metadata: {
                concurrentRequests: '30'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    // Ensure AcrPull is assigned before the Container App starts,
    // otherwise the first replica startup fails with an auth error.
    // Note: Azure RBAC propagation can take 1-2 minutes after assignment.
    acrPullAssignment
  ]
}


// ═════════════════════════════════════════════════════════════════════════════
// Outputs — consumed by azd after Bicep deployment
//
// azd reads these values from the deployment output and uses them to:
//   • Know which ACR to push the built image to      (AZURE_CONTAINER_REGISTRY_ENDPOINT)
//   • Know which Container App to update after push  (SERVICE_QUOTES_API_RESOURCE_NAME)
//   • Display the live URL of the deployed service   (SERVICE_QUOTES_API_URI)
//
// Output names follow the azd convention:
//   SERVICE_<SERVICE-NAME-UPPERCASED-UNDERSCORED>_RESOURCE_NAME
//   SERVICE_<SERVICE-NAME-UPPERCASED-UNDERSCORED>_URI
// ═════════════════════════════════════════════════════════════════════════════

// azd reads this to tag and push the container image during `azd deploy`
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.properties.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = registry.name

// azd reads this to find the Container App and update its image reference
output SERVICE_QUOTES_API_RESOURCE_NAME string = containerApp.name

// The public HTTPS URL of the deployed API — displayed by `azd up` at the end
output SERVICE_QUOTES_API_URI string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
