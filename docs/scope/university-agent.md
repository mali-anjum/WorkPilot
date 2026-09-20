# Epic: University Agent (Slice 2)

Mirrors the Job Agent's discover-to-track loop for academic opportunities, adding real outreach: finding professors whose research actually aligns, and sending real emails through Gmail with the same approval discipline as job applications.

### 21. University/program/professor/scholarship discovery · needs a decision
Discovery across universities, programs, professors, research areas, scholarships, with the same provenance discipline as jobs (source, retrieved/verified dates, confidence). Tabs: Universities, Programs, Professors, Scholarships, Research Areas on `/universities`.
**Done when:** a real search by country/field/degree/funding/research area/deadline returns university/program/professor/scholarship rows with provenance, and a professor detail page shows research interests, publications, and sources.
- [ ] Design it (spec): `/architect university/program/professor/scholarship discovery`

### 22. Research matching engine
Compares user research interests + publications + topics against professor research interests, publications, program, university, funding; outputs alignment, evidence, confidence, missing information (same explainability bar as job matching).
**Done when:** a professor's match includes a "why this matches you" section backed by real evidence, not just a score.
- [ ] Design it (spec): `/architect research matching engine`

### 23. Gmail integration (OAuth) · needs a decision · GA
OAuth connection to Gmail (`/integrations/gmail`), scoped permissions (read/send/draft), account and permission display, disconnect flow. The Agent/LLM never sees the OAuth token directly, only the send-email tool does, gated by the approval engine (feature 8).
**Done when:** the user can connect and disconnect a real Gmail account via OAuth, and permissions are visibly scoped and revocable.
- [ ] Design it (spec): `/architect Gmail integration`

### 24. Outreach pipeline & email composer · needs a decision · GA
`/outreach` pipeline (Candidates -> Drafted -> Ready -> Approved -> Sent -> Replied -> Follow-up -> Closed). Composer shows the generated message plus the evidence used (university profile, research page, publication, user profile) before it can be approved and sent.
**Done when:** an outreach email cannot be sent without going through the approval engine, and every sent email retains the evidence it was generated from.
- [ ] Design it (spec): `/architect outreach pipeline & email composer`

### 25. Email thread tracking & reply detection
Tracks sent/received messages per contact as a conversation; detects replies; surfaces them in the thread view and on the professor/outreach detail.
**Done when:** a real reply to a sent outreach email is detected and shown in the thread without manual polling by the user.
- [ ] Design it (spec): `/develop email thread tracking & reply detection`

### 26. Follow up scheduling
Suggests and (with approval) schedules follow ups on outreach with no reply after a configurable window; ties into Tasks/Calendar (Slice 3).
**Done when:** an outreach email with no reply after N days produces a suggested follow up the user can approve or dismiss.
- [ ] Design it (spec): `/develop follow up scheduling`
