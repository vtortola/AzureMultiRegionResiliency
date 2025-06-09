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

### Content propagation and retries

The source record remains available if a request finishes before propagation. The content processor resumes from saved progress and applies the pending state. If it stops after a destination transaction but before saving progress, that transaction can be replayed without applying the same reaction, comment transition or view event twice.

- **Source changes**
  - Creating a conversation saves its message first. Processing creates its counters and feed copies.
  - Creating a reply saves its author-side conversation first. Processing creates the parent comment and adjusts the parent's comment count together.
  - An edit or deletion changes the source first. The processor updates the parent comment and feeds later.
- **Counters and copies**
  - A reaction's applied state and counter adjustment belong to one transaction. Unlike records remain stored so delayed likes can be rejected by date.
  - Parent comment and feed counters receive absolute integer snapshots. Older snapshots are ignored, including when the latest total is zero.
  - Reads tolerate missing counters while initial processing is pending. Session consistency does not make these application copies immediate.
- **Deletion and retention**
  - Latest-version change feeds do not report physical deletions. Content deletion therefore saves a retained marker that readers hide and processors propagate.
  - Content markers, applied reactions, view events and projections currently have no automatic expiry. This supports replay after a long interruption, at the cost of growing storage. Cleanup needs a separate retention design.

## Background processing and regional ownership

Each content processor reads the full Contents change feed, but handles only documents matching its assigned processing region. Checkpoints are separate for each processor, processing region and feed range. Pages containing only other regions' documents still advance its position.

- **Progress**
  - A checkpoint is saved in Account only after the batch completes. A failed range stops its sibling ranges so the job can restart from saved progress.
  - Several destination transactions may already have succeeded before a failure. Their applied state makes the next attempt safe.
- **Leases**
  - Account leases select one active replica for each processor and processing region. Checkpoint writes verify the current lease in the same Account transaction.
  - Content and conflict processors renew separate leases. Losing access to Account can stop them even while User data remains writable.
- **Covering another region**
  - Both processors use the processing-region override. Moving API traffic does not reassign this setting or the ownership of existing content.
  - A replacement stack can consume the same assigned region and resume its checkpoints. Is important to stop the previous owner and confirm replication before this controlled takeover.
  - Automatic takeover during an unconfirmed regional outage is outside the integer-counter guarantee.

## Resolving conflicting writes

Several regions can accept source changes before seeing each other. The Contents container sends unresolved versions to the conflict feed, where the assigned processor selects the later mutable `CreationDate`. The current stored version wins an equal-date comparison. The other regions leave that conflict for its owner. [Microsoft's conflict documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/conflict-resolution-policies) explains the service policies and conflict feed.

The winner is saved with a conditional write before the conflict is removed. If the stored document changes meanwhile, resolution reads again. The content processor receives the winning state through the change stream, but only applies it when its date is later than the state already applied at the destination.

Equal dates leave a limitation in this approach. For example, if a like has already been counted and an unlike with the same date wins conflict resolution, processing ignores the unlike and the count remains one. Comments and copies also reject equal dates, so selecting a source winner does not always make the related documents agree. Conflict resolution does not correct counters directly.

`ProcessingRegion` decides who may process a record; it does not identify where each competing version was written. Dates provide the ordering, with the usual limits of clocks in different regions. Equal dates cannot establish which user action happened last.

Counters are integer snapshots maintained by the assigned content processor. Conflict resolution selects the newer snapshot rather than adding two totals. This relies on exclusive processing and controlled takeover; it is not a counter model for independent writers in several regions.

## Feeds

The content processor copies conversations and counters into each follower's partition. This fan-out makes reads simpler: a paged query can return the feed without gathering content from every followed user.

Popular authors create more copying work and storage. Content and counts propagate independently, so a feed can show a message before its latest counters. The system accepts this delay instead of making the user's request wait for every follower's copy.

## Packaging

The application and its runtime dependencies are packaged in one container image and distributed through Azure Container Registry. Each image has a content hash; a repository name and tag provide a convenient version reference. The prototype uses the short source commit hash as the tag.

The image must match the hosting CPU architecture. An image built on an ARM development machine does not necessarily run on an x86 host, so the prototype explicitly builds for `linux/amd64`.

Packaging keeps regional deployments consistent, but they do not all update at the same instant. Old and new versions may read and write the same documents while deployment proceeds. Compatibility therefore includes stored data, counter formats, recovery records and background processing, as well as the API itself.

The integer-counter design expects fresh data. There is no migration from regional counter maps, and the old and new processors must not overlap during rollout. Preparing a fresh environment and controlling processor takeover are separate operational steps.
