# 0020. Notifications: rationale

The decision record behind [index.md](index.md).

## Context

Approvals (spec 0007) wait silently in `/approvals`: nothing tells you one exists. A failed agent run is visible only in the database. Matching (spec 0019) will find strong jobs in the background with no one looking. The single user needs one place that says "this needs you" and a signal that follows every page.

Spec 0018 settled the mechanics: sources publish events (`ApprovalRequested`, `AgentRunFailed`, `JobMatched` already exist or land in #11), delivery is at least once through `app.outbox_messages`, and `outbox_deliveries` makes each (message, handler) run once. Handlers write only their own module's tables and run inside `HandleEventJob`'s transaction. The Notifications module owns the existing `Notification` table (`ProfileId`, `Type`, `Payload`, `ReadAt`, soft delete), which has no priority, title or link.

The Web and the Api are separate processes and the Blazor app renders InteractiveServer (spec 0016): a component can run a timer for as long as its circuit is open, but the Api cannot push into it without a new channel. One ingestion can push dozens of jobs over the threshold at once. Application outcome events (#16 to #18) do not exist yet.

## Options considered

### Option 1: In app notifications, bell polls every 15 seconds (chosen)

Handlers write rows; the bell asks the Api for the unread count on a timer while the circuit is open.

**Pros**: no new infrastructure or channel; works with the existing internal Api and auth; "real time" within seconds; trivial to test.
**Cons**: up to 15 seconds of delay; one small query every 15 seconds per open tab.

### Option 2: SignalR push from the Api

The Api hosts a hub; the Web circuit subscribes and receives new notifications instantly.

**Pros**: instant; no polling load.
**Cons**: a second realtime channel between two processes, its own auth, reconnect and backpressure handling; handlers would have to push after commit, which the outbox model (database writes only) does not allow cleanly.

### Option 3: Refresh on navigation only

**Pros**: cheapest.
**Cons**: not real time; an approval waits unnoticed while you stay on one page, which fails the done bar.

## Rationale

The done bar is "fire in real time off real events" for one user. Seconds of delay meet it; the outbox already adds up to a minute in the worst case (spec 0018), so a push channel would not make delivery truly instant anyway, only the last hop. Option 2's extra channel costs more to operate than it saves. The digest answers the one real noise source (a large board crossing the threshold at once) at the handler, where the event arrives, rather than making every consumer filter. Email or push delivery is left for after Gmail (#23), since sending is an approval gated action (spec 0007) and handlers must not have side effects outside the database.

