# SocialApp

## Overview

SocialApp is a small Twitter-like application used to explore the challenges of running in several Azure regions. Users can register, log in, follow other users, publish conversations, add comments and react with a like. They can read conversations and a feed of updates from people they follow. A comment can also be a conversation with its own replies.

These features create useful problems for the experiment: keeping email addresses unique, updating related data together, copying data across partitions and handling simultaneous writes. The application uses C# and ASP.NET Core, but the explanations focus on the approach and its limits.

## Cosmos DB

### Documents, partitions and transactions

A Cosmos DB account contains databases, and each database contains containers of JSON documents. The account controls settings such as replication regions and the default consistency level. Within a container, the combination of a partition key and document identifier identifies one document.

Documents with the same partition key belong to a logical partition. A physical partition hosts one or more logical partitions. For the partitioning model used here, the limits considered in the experiment are 2 MB per document and 20 GB per logical partition. Grouping everything together would simplify transactions but would also concentrate storage and traffic.

Cosmos DB supports ACID transactions: atomicity, consistency, isolation and durability. Within one logical partition of one container, a transaction's changes succeed or fail together. Sharing a database, or even using the same partition-key value in different containers, does not extend this boundary. [Microsoft's transaction documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/database-transactions-optimistic-concurrency) explains this scope.

An ETag identifies a document version. A write can include the ETag from an earlier read. If that version has changed, Cosmos DB rejects the write, allowing the application to read again and retry. This optimistic concurrency check protects a read-and-update sequence. It does not prevent two write regions from accepting conflicting changes before replication brings them together.

### Replication and consistency

An account can have one write region with read-only copies elsewhere, or several regions that accept writes. Additional copies also exist inside each region. With several write regions, an application instance can write nearby, but another region may not yet have received the change.

The consistency level describes what reads can return during replication. For the account settings used here, an operation can request a weaker level than the account default, but cannot request a stronger one.

- **Strong**
  - Reads return the latest committed version. This level cannot be combined with multiple write regions.
- **Bounded staleness**
  - Limits how far reads can fall behind writes, by time or number of versions. Keeping this limit can restrict writes when replication falls behind.
- **Session**
  - Within a database session, reads respect the client's previous reads and writes. Session tokens carry that progress between requests.
- **Consistent prefix**
  - Reads can be behind. Changes committed together in a transaction remain visible together; separate document writes do not gain a shared transaction or session guarantee.
- **Eventual**
  - Reads have no guarantee of freshness or order while replicas catch up. A later read can return an older version.

These guarantees have different latency, throughput and availability costs. [Microsoft's consistency guide](https://learn.microsoft.com/en-us/azure/cosmos-db/consistency-levels) describes their scope. A consistency level governs database reads; it does not make the application finish a second write in another partition.

### Change feed and conflict feed

Two mechanisms help with different problems:

- **The change feed reports changed documents.**
  - A worker uses these changes to update related documents, such as copies in followers' feeds.
  - The prototype reads the latest-version mode. Several edits between reads can appear as one latest state, so this is not a complete history of operations.
  - Physical deletes do not appear in this mode. An item that has been removed is also removed from this feed.
  - A continuation token records where reading can resume. Ordering is per partition key, not across the whole container.
- **The conflict feed exposes versions that need custom resolution.**
  - Writes in different regions can produce competing versions of the same document.
  - The application needs rules that decide which information survives or how contributions are combined.

The all-versions-and-deletes mode was in preview during the original experiment. It can include intermediate changes and deletions, requires continuous backup and has retention limits tied to that backup. The prototype uses latest-version processing. [Microsoft's change-feed documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/change-feed) explains the modes and regional behaviour.

### Query design and cost

The model places documents around common reads. Comments sort before their conversation header, so a range can retrieve the header, counters and latest comments together. Earlier comments use the previous comment identifier as a pagination boundary. Identifiers include a time component, allowing creation-order sorting.

Indexes make these reads practical, but maintaining them also costs database work. The prototype keeps indexing limited: identifier lookups support reservations and sessions, sort-key indexes support content and feed reads, and a composite index combines a root-conversation marker with the sort key.

The root marker distinguishes conversations on the main wall from conversations that represent replies. Default values such as false, zero and null are generally omitted from stored documents. This keeps the representation small, but does not mean missing properties disappear from every index: Cosmos DB stores an undefined value for missing composite-index paths. The benefit here is the targeted query and indexing policy. [Microsoft's indexing documentation](https://learn.microsoft.com/en-us/cosmos-db/indexing-policies) explains this behaviour.

Request Units (RU) measure the database work needed for an operation. A point read of a 1 KB document by partition key and identifier costs one RU with Session or weaker consistency; stronger read guarantees cost more. Query costs depend on the work performed, while write costs also depend on document size and indexing. The physical-partition throughput limit considered in the experiment was 10,000 RU/s.

Capacity choices have different cost patterns:

- **Standard provisioned throughput**
  - Capacity is reserved in RU/s and paid for during quiet periods as well. Requests above available capacity can be throttled and require retries.
- **Autoscale**
  - Capacity can vary between 10% and 100% of a configured maximum. A higher unit price can be useful when demand varies considerably.
- **Serverless**
  - Charges follow consumed RUs without reserved throughput. This can suit occasional use, but serverless accounts support only one region, so they do not provide the replicated setup explored here. See [Microsoft's serverless documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless).

For East US 2 recorded hourly equivalents at 100 RU/s of $0.008 for Standard, $0.012 for Autoscale and $0.09 for Serverless, with storage around $0.25 per GB per month. These are historical comparison figures, not current prices or minimum purchasable capacities.

Several regions add throughput, storage and replication costs. Feed copying also adds writes, and each regional worker reads the full change feed even though it processes only its assigned documents. The prototype adds up database request charges per business operation to make these costs visible.

## Data guarantees

Account, User and Session data use separate Cosmos DB accounts and databases. This separation matters because replication and default consistency are account-level choices.

- **Account: global reservations and coordination**
  - One write region reserves email addresses and public handles. The partition key and identifier make each reservation unique at that write location.
  - Consistent Prefix is used, with read-only copies in other regions. A preliminary read can be stale; the final reservation succeeds or fails when written.
  - Pending registrations record unfinished work. Their identifiers allow older work to be found for cleanup.
  - Worker checkpoints and leases also live here. A lease records which application replica may run a processor and when that permission expires.
- **User: replicated application data**
  - Multiple write regions accept changes, using Session consistency.
  - Profiles and login credentials are available through replication. A handle-to-user mapping duplicates reservation information for convenient reads.
  - Follow relationships have a list on each user's side. Content and feed copies are stored separately from profiles.
  - Conversations, comments and feed entries keep their message content separate from their counters, allowing different conflict rules.
  - Reaction records identify the user and target, while a source reaction record allows background processing to finish related updates.
- **Session: regional login state**
  - Each region has its own database with Consistent Prefix and no cross-region replication.
  - A login creates a generated session identifier, and the application retrieves the session by that identifier.
  - Sessions include the user identity, display details and storage for Cosmos DB session tokens. Time to live (TTL) limits how long an inactive session remains available.

An application login session and Cosmos DB Session consistency are different concepts. The first identifies the user across HTTP requests. The second tracks database progress. Keeping login sessions regional means that moving to another region can require a new login, and previous session state is not carried there automatically.

## Recovering operations across partitions

### Registration and global uniqueness

Two write regions could both accept the same email reservation before replication detects a conflict. Removing one account after both registrations appeared successful would be a poor business outcome. Account therefore uses one write region to decide uniqueness.

Putting every reservation in one partition would allow a larger registration transaction, but all accounts would share that partition's storage and throughput limits. The prototype instead uses separate keys and records the unfinished operation:

- **Before registration**
  - A pending record saves the email, handle, user identifier and creation time.
- **During registration**
  - The service reserves the email and handle, then creates the handle mapping, login credentials and profile.
  - These writes cross partitions and databases. A failure can leave only some of them stored.
- **After registration**
  - The pending record is removed when registration finishes.
  - Cleanup examines old pending records. An existing profile is treated as completed registration; otherwise, cleanup removes remaining data and reservations belonging to that attempt.

The pending record follows the useful idea of saving work before performing it. This is a recovery record, rather than a complete transactional outbox: the record and all business changes are not committed together in one transaction. Recovery also depends on the availability and consistency of the data being checked.

### Login and session lifetime

Login validates replicated credentials and creates one regional session document. Several active sessions per user are allowed. The API returns an encrypted authentication cookie containing the session identifier, and later requests use it to load the session.

Session operations stay within one regional document:

- **Recovery and renewal**
  - A missing session is unavailable. Sessions have a one-hour TTL.
  - Reading a session more than 45 minutes after its last renewal updates its timestamp and extends its lifetime. Earlier reads avoid this renewal write.
- **Saving database progress**
  - Token saves patch the existing document and update the renewal timestamp together. Any document write resets Cosmos DB TTL.
  - Saving does not recreate a missing session.
- **Logout**
  - The session document is deleted. Repeating logout after deletion needs no further change.

Invalid credentials are an authentication failure; a database connection problem remains a server failure. These operations avoid a transaction across partitions, but still depend on the regional session store and access to replicated login data. A recently created account may not yet be visible after a change of region.

### Following another user

A follow relationship appears in two partitions: user A's following list and user B's follower list. A transaction cannot cover both.

- **Following**
  - A's list first records B as a pending addition.
  - B's list then records A as a follower.
  - A's entry is finally marked ready.
- **Unfollowing**
  - A's entry is first marked as a pending removal.
  - A is removed from B's follower list.
  - The entry is then removed from A's list.
- **Recovery**
  - Reading A's following list returns only ready entries and leaves pending entries unchanged.
  - A later follow operation by A reconciles A's pending entries only.
  - Reconciliation checks each pending entry against the other user's follower list. If that list contains A, A's entry becomes ready; otherwise, A's entry is removed.
  - Repeating an already completed follow or unfollow is accepted.

### Conversations, comments and reactions

The same message can appear as an author's conversation, a comment under another conversation and copies in followers' feeds. The model uses local transactions where possible and background repair for related partitions.

- **Creating a root conversation**
  - The message and its counters are created together in the author's partition.
  - Feed copies are created later by the change-feed worker.
- **Adding a comment**
  - A reply is stored under the parent conversation and as a conversation in its author's partition. Both representations have counters.
  - If processing stops after the parent comment is saved, the worker can create the missing author-side conversation and counters.
  - Parent references identify the relationship and prevent repeated creation from becoming an endless copying loop.
- **Editing a conversation**
  - Only its owner can edit it. The source conversation is updated first.
  - If it also represents a comment, the parent comment needs a separate update. The worker checks and repairs that update, even when the request already attempted it.
- **Reacting**
  - A reaction and the affected counters are changed in partition transactions.
  - A reaction involving a comment can require changes in another partition. The saved source reaction lets the worker finish propagation.
- **Reading**
  - Conversation queries combine message content, counters and recent comments. Root-conversation queries filter by the root marker, and pagination uses ordered identifiers.
  - Session consistency helps reads respect previous database progress, but does not make unfinished copies current.

### Deletions need time to propagate

Latest-version change-feed processing cannot discover a physical delete. The application first marks a document as deleted and assigns a TTL. Queries hide marked documents, workers copy the deletion state, and Cosmos DB eventually removes the documents.

This creates a recovery window. The deletion marker must remain available longer than the delay before its consumers catch up. If an outage or processing backlog lasts beyond the TTL, the marker can disappear before a worker reads it. The prototype therefore cannot promise complete deletion propagation after an arbitrarily long interruption.

## Background processing and regional ownership

The API hosts background jobs for pending-registration cleanup, content propagation and custom conflict resolution. They continue work that cannot depend on the original HTTP request remaining alive.

The content worker processes source changes as follows:

- **Conversation changes**
  - Update followers' feed copies and the parent comment when one exists.
- **Conversation counter changes**
  - Update corresponding comment and feed counters.
- **Source reaction changes**
  - Check that the reaction reached the relevant conversation and comment counters.
- **New comment changes**
  - Check that the author's corresponding conversation and counters exist. This conversation represents a reply and is excluded from the main root-conversation wall.

Processing saves progress only after the page succeeds. A failure before checkpointing can cause already completed work to repeat. The handlers therefore need to tolerate retries; a checkpoint does not provide an exactly-once transaction covering all destination writes.

### Ownership and checkpoints

Each region's worker reads the full content change feed, but only processes documents assigned to that region. Ownership is set when a source document is created and is preserved during edits and conflict resolution.

For example, a conversation created in East US 2 remains assigned there after an edit from West US 2. This avoids both regions independently processing every source change, but adds delay for edits made elsewhere. During a regional outage, traffic moving to another API does not move ownership of older content.

Each worker keeps a separate continuation token for each feed range. Its checkpoint identity includes the processor, processing region and range. Pages containing only another region's documents still advance that worker's position.

Checkpoints are stored in Account so their replicated copies can survive the loss of the worker's region, subject to Account replication and recovery limits. Secondary-region workers must write progress to Account's current single write region. This adds remote writes and creates a dependency on Account availability.

### Leases

Several API replicas can run in one region, but a lease selects one active replica for each processor and processing region. The owner renews its permission regularly. If it stops or cannot renew, processing is cancelled and another replica can acquire the lease after expiry. Content checkpoint writes also verify lease ownership in the same transaction, preventing an old owner from advancing progress after losing its lease.

Content and conflict processors both use leases in Account. If Account writes become unavailable, workers can lose their leases and stop even while the User database still accepts writes. Replication does not make this coordination dependency disappear.

The content worker supports an explicit processing-region override. A worker elsewhere can use it to resume the failed region's content work from saved progress after acquiring the corresponding lease. The deployed environment does not automatically arrange this takeover. Keeping progress durable and deciding where replacement workers run are separate responsibilities.

## Resolving conflicting writes

Cosmos DB offers Last Write Wins (LWW), which selects one version, and custom resolution. Custom resolution can run in a stored procedure or through application code reading the conflict feed. The policy applies to a container, so different document types in that container still need suitable rules. [Microsoft's conflict documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/conflict-resolution-policies) describes these options.

The prototype configures custom resolution for the content container. Its application merger handles conversation messages and conversation counters. Comment and feed counters carry the same regional contribution format, but are normally updated from their source conversation. This is not complete custom conflict handling for every document type: other content conflicts can remain unresolved, while Profile and Feed containers use the default policy.

### Message content

Only the owner can edit a conversation, so simultaneous edits from different regions are expected to be uncommon. The merger keeps the message with the later modification time and preserves processing ownership.

This selects one edit; it does not combine both messages. Timestamp-based selection also depends on clocks and needs a clear rule for ties before it can be treated as a general solution. The prototype's counter merge has stronger convergence properties than its message-selection rule.

### Distributed counters

Selecting one counter total would lose increments accepted in another region. Instead, each like, comment or view counter stores two sets of regional totals: increments and decrements. This is a PN-counter, named after positive and negative contributions.

- **Adding a contribution**
  - Adding a like increases the current region's increment total.
  - Removing a like increases its decrement total. Both stored components only grow.
- **Calculating the visible count**
  - The count is the sum of increments minus the sum of decrements.
- **Merging versions**
  - For each region, the merger takes the larger increment total and the larger decrement total.
  - A missing regional entry means zero.

For explaining the merge, the example starts with five likes added in East US 2 and already copied to West US 2. Each region then adds one like before receiving the other's update:

- **Shared starting state**
  - East US 2 contribution: 5.
  - Visible total: 5.
- **East US 2 after its new like**
  - East US 2 contribution: 6.
- **West US 2 after its new like**
  - East US 2 contribution: 5.
  - West US 2 contribution: 1.
- **Merged state**
  - East US 2 contribution: 6, the larger of 5 and 6.
  - West US 2 contribution: 1.
  - Visible total: 7. The shared first five likes are counted once.

The merge is commutative, associative and idempotent: version order does not matter, grouping does not matter, and processing the same contributions again does not change the result. It does not need the last version shared by both regions.

Within one region, atomic increments in a partition transaction prevent simultaneous requests from overwriting each other's contribution. During conflict resolution, the merger also reads the current stored counters and includes them. An ETag check prevents replacing a version changed since that read; on rejection, the merger reads again and retries.

Is important that each region keeps a stable contribution key and that writes sharing that key remain coordinated through the same Cosmos DB write region. The application derives this key from its configured region. Database rerouting during failover needs particular care, because the configured application region alone does not prove where a write was handled. The local-development fallback assumes one region.

Counter dictionaries start empty, allowing the first contribution from a region to be added. Entries remain stored even when increments and decrements cancel out, so a total returning to zero can also reach comment and feed copies. The merge protects recorded contributions; it does not by itself make every business reaction exactly once.

The document format differs from the earlier format that stored only totals. The prototype expects those old documents to be recreated and has no migration for them. A live service would need a compatible transition before deploying this format across regions.

## Feeds

A background worker copies conversations and counters into each follower's partition. This is called fan-out. Reading the feed then uses one paged query instead of gathering content from every followed user.

This makes frequent reads simpler, but popular authors can generate many writes for each update. Copies also use extra storage. A newly saved conversation or counter change may still be waiting for propagation, even when the feed query uses Session consistency. Database session guarantees cannot remove an application-level copying delay.

## Packaging

The application and its runtime dependencies are packaged in one container image and distributed through Azure Container Registry. Each image has a content hash; a repository name and tag provide a convenient version reference. The prototype uses the short source commit hash as the tag.

The image must match the hosting CPU architecture. An image built on an ARM development machine does not necessarily run on an x86 host, so the prototype explicitly builds for `linux/amd64`.

Packaging keeps regional deployments consistent, but they do not all update at the same instant. Old and new versions may read and write the same documents while deployment proceeds. Compatibility therefore includes stored data, counter formats, recovery records and background processing, as well as the API itself.
