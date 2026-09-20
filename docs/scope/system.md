# Epic: System

Cross-cutting screens that make the product feel like one coherent system rather than three bolted-together agents.

### 30. Settings
Profile, Preferences, Job Preferences, University Preferences, AI, Integrations, Notifications, Security, Privacy, Automation, Appearance, Data.
**Done when:** every section persists real settings that other features actually read (job preferences feed matching, automation policy feeds the approval engine's defaults, appearance toggles light/dark).
- [ ] Design it (spec): `/develop settings`

### 31. Integrations hub
`/integrations` cards for Gmail, Google Calendar, job sources, university sources, AI providers, browser worker; each shows connected/disconnected, account, permissions, last sync, last error.
**Done when:** every real integration built elsewhere in the scope (Gmail, calendar, at least one job source) is visible and manageable here, not just listed statically.
- [ ] Design it (spec): `/develop integrations hub`

### 32. Command palette & global search
Ctrl+K palette with the action list from the product spec (find jobs, create application, find university/professor, draft outreach, check email, create task, schedule follow up, open approvals/activity); global search grouped by entity type across jobs, applications, universities, professors, emails, tasks, agent runs, activity.
**Done when:** Ctrl+K opens from anywhere in the shell and both action commands and search results resolve against real data.
- [ ] Design it (spec): `/develop command palette & global search`

### 33. Onboarding flow
Welcome -> Goals -> Profile -> Resume -> Job preferences -> Research interests -> Gmail -> Calendar -> Automation policy -> Ready. Default automation policy is approval-based.
**Done when:** a fresh account can complete onboarding and land with a usable profile, at least one resume, and an explicit automation policy choice recorded.
- [ ] Design it (spec): `/develop onboarding flow`
