# Azure infrastructure

## Overview

This environment explores how Azure services support an application running in several regions. The example uses East US 2 as its primary region and West US 2 as its secondary region. 

Terraform manages the resources and their dependencies as one environment. It is a tool for describing and applying infrastructure configuration. The useful lesson here is which resources need repeating in each region, which services replicate data, and which dependencies remain shared.

[Deployment instructions step-by-step](terraform/README.md).

**This is a research prototype.** It demonstrates design choices and their limits, rather than a complete production deployment.

<img src="../diagram.jpg" width="500" alt="Azure services and their connections across regions">

## Services and regional responsibilities

### Hosting the application

Each region needs its own running application instances. Replicating a database or a container image does not create these instances.

- **Azure Container Apps**
  - Hosts the primary region's API without requiring server management. It provides load balancing, autoscaling and gradual deployments through application revisions.
  - It uses Kubernetes internally while exposing a simpler hosting model. Each regional deployment still needs its own configuration.
  - It was the preferred option for a real application. [Microsoft's Container Apps overview](https://learn.microsoft.com/en-us/azure/container-apps/overview) describes the service.
- **Azure Container Instances**
  - Hosts the secondary region's API with a small amount of setup and a public DNS name.
  - It does not provide the same built-in application load balancing, autoscaling or gradual deployment features. [Microsoft's Container Instances overview](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-overview) explains this hosting option.

Student subscription restrictions prevented the original experiment from using the required Container Instances setup in several regions. The demonstration therefore uses Container Apps in East US 2 and Container Instances in West US 2. This difference is an experimental constraint, not a requirement of multi-region design.

### Replicating data with Cosmos DB

[Azure Cosmos DB](https://learn.microsoft.com/en-us/azure/cosmos-db/introduction) stores the application's JSON documents. Its replication and consistency choices are configured at account level, so Account, User and regional Session data use separate accounts and databases.

- **Account data**
  - Has one write region and read-only copies in the other configured regions, with Consistent Prefix consistency.
  - Reserves unique email addresses and handles. A request from a secondary API must still reach the current Account write region for the final reservation.
  - Also stores pending registrations, background-worker leases and processing checkpoints.
- **User data**
  - Accepts writes in several regions, with Session consistency. This arrangement is often called active-active replication.
  - Holds profiles, follow relationships, conversations and feeds. Local application instances can read and write nearby, while changes travel asynchronously between regions.
  - Requires application decisions for conflicts and for related copies that have not yet caught up.
- **Session data**
  - Each region has a separate store with Consistent Prefix and no replication to another region.
  - Moving a user to another API region can require another login. This is an accepted simplification of the prototype.

The primary region is the initial Account write location, not a permanent write location after failover. The image registry and hosting resources do not move simply because Cosmos DB promotes another Account write region.

### Distributing application images

[Azure Container Registry](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-geo-replication) stores the application images and replicates them to secondary regions. The prototype uses the Premium tier for geo-replication. An image can then be downloaded near the region where it will run.

Each application deployment selects a repository and version tag. The prototype uses the short source commit hash as that tag. Publishing a new image and copying it to another region only makes the image available; the running containers change when a deployment selects that version.

Registry replication and availability-zone redundancy solve different problems. The former places copies in different Azure regions. The latter protects against failures within a region. The prototype's registry and Cosmos DB settings do not enable zone redundancy.

### Configuration, identity and logs

These resources are set up separately in each region:

- **Resource groups**
  - Group related hosting, configuration and logging resources for management. A resource group does not replicate its contents. See [Microsoft's resource-group documentation](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/manage-resource-groups-portal).
- **Key Vault**
  - Holds configuration and secrets that the application reads at startup, including its database configuration.
  - The deployment fills each regional vault with the required values. This repeated setup keeps configuration aligned; it is separate from database replication.
  - The prototype uses Standard vaults, allows public network access and keeps soft-deleted vault data for seven days. These are experiment settings. See [Microsoft's Key Vault overview](https://learn.microsoft.com/en-us/azure/key-vault/general/overview).
- **Managed identities**
  - Give each region's service an Azure identity with permission to read configuration and pull images. The registry permission is the `AcrPull` role.
  - Adding a region also requires its identity and role assignments. See [Microsoft's managed-identity overview](https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview).
- **Log Analytics workspaces**
  - Collect logs for the regional deployment. Investigating an operation that crosses regions requires examining the affected workspaces.
  - A regional API responding successfully does not show whether another region's background work is delayed. See [Microsoft's Log Analytics overview](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/log-analytics-workspace-overview).

## One global address, several regional endpoints

Azure DNS holds the environment and regional names. Traffic Manager uses DNS responses to direct clients to an API endpoint. It does not forward the application requests: after resolving the name, the client connects directly to the selected endpoint.

The demonstration's names show the hierarchy:

- **Parent domain: `socialapp.x81.io`**
  - The environment zone is `test.socialapp.x81.io`.
  - The global API name is `api.test.socialapp.x81.io`, an alias for Traffic Manager.
- **Primary regional zone: `eastus2.test.socialapp.x81.io`**
  - Its API name is `api.eastus2.test.socialapp.x81.io`, pointing to the Container Apps endpoint.
- **Secondary regional zone: `westus2.test.socialapp.x81.io`**
  - Its API name is `api.westus2.test.socialapp.x81.io`, pointing to the Container Instances endpoint.

Name server records delegate responsibility from the parent domain to the environment zone, then to each regional zone. CNAME records make the API names aliases for the service endpoints. The complete endpoint name is also called a fully qualified domain name (FQDN). [Microsoft's DNS overview](https://learn.microsoft.com/en-us/azure/dns/dns-overview) explains these concepts.

The example domain belongs to the original project. Another environment needs its own parent zone and delegation. Regional API names are useful for checking a particular deployment, while the global name lets clients use the application without selecting a region themselves.

### Geographic preference and healthy endpoints

The routing configuration makes two decisions:

- **A Geographic parent profile chooses a regional preference.**
  - In the example, Africa, Europe and the Middle East are assigned to East US 2.
  - North America, South America, Antarctica and Australia/Pacific are assigned to West US 2.
  - These assignments do not cover every geographical area. They are demonstration choices, not a claim that those Azure regions are closest to all assigned users. Geographic routing uses the location associated with the DNS query, which can reflect the client's DNS resolver.
- **A nested Priority profile chooses an available API.**
  - Each child contains all regional APIs. It prefers its assigned region, then the primary region if different, then the remaining regions alphabetically.
  - With the default two regions, each child prefers its own API and falls back to the other.
  - The parent requires at least one healthy endpoint in the child.

Geographic routing alone does not fail over to another geographically assigned endpoint. Nesting it with Priority routing separates geographic preference from health-based selection. [Microsoft's routing documentation](https://learn.microsoft.com/en-us/azure/traffic-manager/traffic-manager-routing-methods) explains the methods.

Both profile levels use a 30-second DNS time to live (TTL). Each child checks HTTP port 7000 at `/ready` every 30 seconds, with a five-second timeout and three tolerated failures. These settings are not a promise of recovery within 30 seconds: health detection, DNS caches and existing connections all affect when traffic moves.

When the preferred API becomes healthy again, new DNS resolutions can select it again. Existing clients may remain elsewhere until their cached result or connection changes.

### Health checks

The application exposes two different checks:

- **Liveness at `/health`**
  - Shows that the API can respond and reports deployment information, including its region and image tag.
  - Hosting uses it for liveness, and Container Apps also uses it during startup.
- **Readiness at `/ready`**
  - Attempts to read the metadata of the configured User and Session containers, with a three-second overall limit.
  - Returns an unavailable response if those checks fail. Both hosting options and Traffic Manager use readiness checks.
  - Does not check Account availability, perform a business-data write or verify that background processing has caught up.

A healthy readiness response therefore supports a routing decision, but does not prove that registration, conflict resolution or feed propagation can complete. This distinction matters when a shared dependency fails while regional APIs remain reachable.

## Recovery during a regional failure

### API recovery and database recovery

Traffic Manager can direct new client connections to another API while keeping the same global name. Cosmos DB independently manages access to database replicas and the Account write region. An API failure alone does not trigger database failover.

Account has service-managed failover enabled. Its initial primary region has the highest preference, and secondary regions follow in alphabetical order. When Microsoft initiates failover for a write-region outage, an available secondary can be promoted according to that order. With the default setup, West US 2 is the alternative to East US 2.

The Cosmos DB client library can discover the new write region through the same account endpoint. Account remains a single-write-region account, and its operations can fail while promotion completes. Writes that had not reached another region may be lost under the selected consistency level. [Microsoft's Cosmos DB reliability guide](https://learn.microsoft.com/en-us/azure/reliability/reliability-cosmos-db) describes the guarantees and recovery options.

Account recovery affects more than registration. Its leases and checkpoints coordinate background work in all regions. If workers cannot renew leases or save progress, processing can stop while the User database remains writable. The readiness endpoint does not detect this condition.

### User sessions

A user moved to another region can reach replicated User data, subject to replication progress, but cannot use the previous region's session document. The recovery objective accepts a new login. A later return to the preferred region can require another login as well.

Content ownership also stays with the region where the source document was created. Replicated checkpoints allow a replacement worker to resume saved progress, but the prototype requires an explicit processing-region override to take over another region's content work. Replica replacement within a processing region is handled by leases; regional reassignment is not arranged automatically by the deployment.

Recovery therefore has several separate conditions:

- **Clients can reach an API.**
  - Health checks and DNS routing select a responding endpoint.
- **Required data operations are available.**
  - Regional sessions can be created, User data is accessible and Account has an available write region when needed.
- **Incomplete work can continue.**
  - Workers hold valid leases, resume their checkpoints and catch up with changes.
  - Deletion markers must still exist when workers process them; TTL limits this recovery window.
- **Business state remains acceptable.**
  - Registration reservations and related user records need checking after possible data loss.
  - Restored routing alone does not establish that acknowledged writes, counter contributions or all copied updates survived.

The recovery time objective (RTO) concerns how long an operation can remain unavailable. The recovery point objective (RPO) concerns how much recent data can be lost. The prototype demonstrates mechanisms that affect both, but does not establish measured end-to-end recovery guarantees.

## Adding or removing a region

One deployment configuration manages the primary and selected secondary regions together. Adding a region involves several coordinated changes:

- **Replicated services**
  - Account receives another read location, User data receives another write location, and the registry receives an image replica.
- **Regional resources**
  - The new region needs hosting, a Session database, a resource group, an identity, configuration and logs.
- **Routing**
  - Regional DNS records and Traffic Manager endpoints are created. Geographical assignments determine which clients prefer the new region.
- **Application support**
  - The current application recognises only `eastus2` and `westus2` as deployment regions. Extending the infrastructure's region list alone is not enough to run in another region.
  - Processing ownership and stable counter contribution keys also need to remain meaningful after a region changes.

A primary-only environment is possible, but has no alternative regional API. Its geographical assignments need to include the clients previously assigned elsewhere. Similarly, removing a region needs consideration of its owned content and regional sessions, rather than only deleting its resources.

## Deployment approach

### Preparing the environment

The deployment separates shared prerequisites from resources created for the experiment:

- **State storage**
  - An existing Azure Storage backend holds Terraform's record of managed resources. Each independent environment needs a separate state key.
  - Losing track of this state is different from losing application data, but affects the ability to manage and recover infrastructure safely.
- **Domain ownership**
  - An existing parent Azure DNS zone is delegated from the domain provider. The stack creates environment and regional zones below it.
  - Resource names and the registry name must be available in Azure.
- **Subscription and permissions**
  - The deployment identity needs access to the state backend, parent DNS zone and resources, including permission to assign roles.
  - Azure resource providers expose service types in a subscription. Their automatic registration is disabled in this prototype, so the required providers must already be registered.
- **Build tools**
  - The experiment uses Azure CLI, Terraform, Git and Docker with Buildx. A local image build needs Docker running.
  - The selected subscription, environment names, regions and geographical assignments must agree before resources are created.

The default environment is named `test`, uses `socialapp` as its solution name and deploys the two example regions. Region selection, domain settings and image version are deployment inputs. Their purpose is to repeat the same arrangement without manually configuring each service in each location.

### Creating and updating deployments

Initial deployment creates the registry, builds the application image from the local checkout and publishes it before the hosting resources select it. The build explicitly targets `linux/amd64`, so the result matches the hosts even when development runs on an ARM machine.

For later updates, a new image is published and its tag is selected for deployment. The registry distributes image content while the deployment updates the application instances. Availability of an image in one region does not prove that every other region can already download it.

The original experiment sometimes needed another deployment attempt after an image or DNS record became available. A timeout also did not always mean resource creation had failed in Azure. Recovery required checking the actual resource and deployment state; an existing resource could need to be brought into management rather than created again.

Regional health endpoints show which region and image version respond. Readiness checks cover the limited database access described earlier. Checking the global name then exercises the DNS routing path, while business operations are needed to establish whether the application actually works after deployment.

### Compatible versions across regions

Old and new application versions can run together while regions update at different times. Gradual deployment within one region creates the same issue. Both versions must understand the documents they may read and the changes their background workers may process.

The prototype environment can be removed and recreated because it is an experiment without a live migration requirement. That simplification does not apply to an established service. For example, changing counters from simple totals to regional contributions requires a data transition before both formats can be used safely during deployment.

Removing the experimental stack deletes resources managed by that environment's state. The separately prepared state backend and parent DNS zone remain outside its scope.
