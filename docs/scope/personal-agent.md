# Epic: Personal Agent (Slice 3)

Calendar and tasks, plus the automation that ties interview emails to real calendar events and prep work, closing the loop the other two agents feed into.

### 27. Calendar integration & screen · needs a decision
`/calendar` (Month/Week/Day/Agenda). The Agent can suggest or create events per the approval policy (creating an event is likely approval required unless explicitly configured otherwise).
**Done when:** a real calendar event can be created from the app and appears in the connected calendar, gated by approval.
- [ ] Design it (spec): `/architect calendar integration & screen`

### 28. Tasks screen & task engine
`/tasks` (Today/Upcoming/Waiting/Completed). Task carries title, due date, priority, related object, source, created by, agent suggestion.
**Done when:** a task created by the Agent (e.g. from an outreach follow up or an interview prep suggestion) appears correctly columned and linked to its source object.
- [ ] Design it (spec): `/develop tasks screen & task engine`

### 29. Interview lifecycle automation
Detects an interview from an email (job application context), creates a calendar event and a prep task, notifies the user; tracks through to a post-interview follow up task.
**Done when:** a real interview-scheduling email produces a calendar event and a prep task without manual entry, both traceable back to the source email.
- [ ] Design it (spec): `/architect interview lifecycle automation`
