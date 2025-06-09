# Exploring Azure multi-region resilience

## Overview

Running in several regions can bring the application closer to its users and reduce its dependency on one location. However, deploying more servers is only part of the work. Data must be available where it is needed. Changes made separately must reach a common state, and interrupted operations need a way to recover. Routing and deployment also need to work across the whole environment.

This is a prototype that uses Cosmos DB, containers in each region, replicated container images and DNS routing to explore the balance between data consistency, availability and response time through a small social application.

**This repository is a scratchpad for research and experimentation.** It is not production code or a complete guide to globally distributed applications.

## The prototype

<img src="./diagram.jpg" width="500" alt="Azure services and regional deployments in the prototype">

### The Azure infrastructure

[The infrastructure guide](./infrastructure/README.md) explains how the Azure services form one environment. Terraform, an infrastructure configuration tool, creates the regional resources and their connections. The important distinction is between services that replicate data and services that need a separate deployment in each region.

### The ASPNetCore service

[SocialApp](./src/README.md) provides practical examples of the problems: reserving a unique email address, updating two users' follow lists, copying conversations into feeds and merging simultaneous counter updates. The application guide explains these decisions without requiring knowledge of C#.

## The design

The design separates responsibilities according to their needs:

- **Account data has one write region.**
  - Email and handle reservations need one place that decides whether a value is already taken.
  - Other regions hold read-only copies. Service-managed failover can promote another write region, but Account operations can be interrupted during recovery.
- **User data accepts writes in several regions.**
  - Profiles, conversations and feeds are replicated. Local access reduces the delay for users, while asynchronous replication allows temporary differences between regions.
  - Session consistency and conflict handling address different parts of this problem. Neither makes related copies update instantly.
- **Login sessions stay in their own region.**
  - This simplifies session storage, but a user may need to log in again after switching regions.
- **Background workers complete related updates.**
  - Pending records, repair during reads and the Cosmos DB change feed help recover work that cannot fit in one transaction.
  - Worker progress and leases are stored in Account, so background processing also depends on its write availability.

## Challenges 

### Transactions across partitions

A Cosmos DB transaction covers one logical partition within one container. Changes outside that boundary can stop after only some writes succeed. The prototype groups related documents where possible and records enough information to complete or clean up interrupted work.

Registration uses a pending record, follow relationships use pending states, and content changes use asynchronous processing. These approaches allow recovery, but related documents can disagree while the unfinished work is being handled.

### Concurrent writes and delayed copies

Several write regions can accept changes before seeing each other's updates. The application therefore needs rules for competing versions of the same document. Selecting one message version can be acceptable, while selecting just one counter total can lose increments from another region.

The prototype keeps message content separate from counters. Counters store each region's contributions so a merge can retain changes from both regions. The change feed also copies content into comments and followers' feeds, accepting extra writes and storage in exchange for simpler reads.

### Regional failures

One global DNS name lets clients reach a regional API. Geographic routing chooses the preferred region, and nested priority routing provides another endpoint when that API becomes unhealthy.

API routing, database failover and background worker recovery are separate processes. A client reaching another API does not prove that Account writes have recovered or that all feed updates have been processed. Replicated checkpoints support worker recovery, but taking over another region's content processing needs an explicit configuration change in this prototype.

The design also accepts that recent writes may not have reached another region before an outage. Recovery needs to consider both the time before operations work again and the changes that might have been lost. These are commonly called the recovery time objective (RTO) and recovery point objective (RPO). Their relationship with Cosmos DB replication is described in [Microsoft's reliability guide](https://learn.microsoft.com/en-us/azure/reliability/reliability-cosmos-db).

### Deployment across regions

Container images are published, copied between regions and then selected for deployment. Copying an image does not update a running application. During deployment, old and new versions can run at the same time and must understand the document formats they share.

The experiment uses Container Apps in East US 2 and Container Instances in West US 2 because of student subscription restrictions. Container Apps was the preferred option for a real application because it supports autoscaling and gradual deployments. Container Instances provides simpler hosting for the experiment.

## Research questions

- Which Azure services support a global application?
  - Which services replicate their data, and which need a separate setup in each region?
- Which operations can accept writes in several regions?
  - How do conflicting versions reach the same state without losing required changes?
- Which operations can continue during a regional failure?
  - Which still depend on one write region, a regional session or a background worker?
- How can incomplete operations recover?
  - What happens if processing repeats, stops for a long time or misses an expired deletion marker?
- How does deployment work across regions?
  - How can different application versions safely share the same data during an update?

## Conclusions

Azure provides the services needed to distribute an application, but replication does not remove the need for application-level recovery. Transaction boundaries, conflicting writes and delayed copies still influence the data model and the behaviour visible to users.

Different data needs different guarantees. A single write region simplifies global uniqueness. Multiple write regions improve local access but require conflict rules. Regional sessions simplify storage but make switching regions visible to users. Background processing reduces the work done during a request, while adding dependencies and a delay before copies catch up.

The prototype makes these trade-offs visible through concrete operations. Its remaining limits are part of the result: a healthy API is only one part of recovery, saved progress does not arrange regional worker takeover by itself, and compatible data formats are necessary while regions run different versions.
