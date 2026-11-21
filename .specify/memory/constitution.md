<!--
Sync Impact Report
- Version change: N/A (uninitialized template) → 1.0.0
- Modified principles:
  - [PRINCIPLE_1_NAME] → I. Architecture and Code Quality
  - [PRINCIPLE_2_NAME] → II. Windows Integration
  - [PRINCIPLE_3_NAME] → III. Performance and Resource Efficiency
  - [PRINCIPLE_4_NAME] → IV. Security and Privacy (Local-First)
  - [PRINCIPLE_5_NAME] → V. User Experience and Accessibility
- Added principles / sections:
  - VI. Engineering Discipline and Project Management
  - VII. Specification-First and Evidence-Based Development (NON-NEGOTIABLE)
  - Architectural & Operational Constraints (mapped from [SECTION_2_NAME])
  - Development Workflow & Quality Gates (mapped from [SECTION_3_NAME])
- Removed sections: None
- Templates alignment status:
  - ✅ .specify/templates/plan-template.md (Constitution Check & Complexity Tracking aligned)
  - ✅ .specify/templates/spec-template.md (Measurable Outcomes, Assumptions & Edge Cases aligned)
  - ✅ .specify/templates/tasks-template.md (Phased milestones & verification gates aligned)
- Follow-up TODOs: None
-->
# Shima Constitution

## Core Principles

### I. Architecture and Code Quality

- The system MUST use a clean, modular, and maintainable architecture that strictly separates UI
  presentation, business and domain logic, data and persistence infrastructure, and Windows
  operating system integrations.
- Components MUST apply SOLID design principles where appropriate, interacting through clear
  abstractions, explicit interfaces, and well-defined module boundaries.
- Shared components and core abstractions MUST NOT contain widget-specific or screen-specific
  logic, and modules MUST NOT introduce circular dependencies or tight coupling.
- External dependencies MUST be minimized; every third-party library MUST justify its inclusion
  against native platform capabilities.
- Technology selection MUST prefer established, production-proven, and stable technologies over
  experimental solutions unless formally justified in an Architectural Decision Record (ADR).

**Rationale**: Strict separation of concerns and a minimal dependency footprint keep the desktop
shell resilient, allow OS integration adapters to evolve independently of UI widgets, and prevent
long-term maintenance decay.

### II. Windows Integration

- The application MUST prioritize reliable, native-grade integration with the Windows operating
  system using documented, supported Windows APIs (Win32, WinRT, Windows App SDK, or COM interfaces
  as appropriate) for system-level functionality.
- Window management MUST deterministically handle multi-monitor environments, per-monitor DPI
  scaling changes, display connect/disconnect events, virtual desktops, full-screen occlusion
  detection, and Z-order positioning.
- The system MUST subscribe and respond gracefully to OS power, display, and session lifecycle
  events (sleep, resume, lock, unlock, and session shutdown).
- Startup registration, system tray behavior, notifications, accessibility hooks, and application
  lifecycle management MUST follow standard Windows platform conventions.

**Rationale**: A desktop utility anchoring to the display edge operates as an extension of the
Windows shell; deterministic handling of display topology and OS lifecycle events prevents visual
glitches, misplaced windows, and orphaned processes.

### III. Performance and Resource Efficiency

- The application MUST minimize CPU, RAM, and GPU consumption at all times so it never degrades
  foreground applications or system responsiveness.
- Background and system monitoring MUST use event-driven processing and OS notification hooks
  wherever possible; continuous busy-waiting and unnecessary polling loops MUST NOT be used.
- Where an OS event hook does not exist and periodic sampling is unavoidable, the timer MUST be
  explicitly justified, bounded, and throttled to the lowest acceptable frequency.
- Non-essential animations, visual effects, and background update ticks MUST be suspended
  automatically when the application or widget is collapsed, idle, occluded, or when the display is
  powered off.
- The application MUST detect battery power and Windows Energy Saver states, automatically reducing
  background activity, frame rates, and visual overhead when running on battery power.

**Rationale**: Always-on desktop software runs for the entire duration of a user session; even minor
idle CPU/GPU wake-ups or memory leaks directly reduce battery life and system performance.

### IV. Security and Privacy (Local-First)

- The system MUST follow a local-first architecture where user preferences, widget state, and
  user-generated content are stored locally on the device by default.
- Sensitive user content—including clipboard contents, personal notes, local file paths, and
  system notification contents—MUST NEVER be transmitted to external servers.
- Network communication MUST be minimized and completely disabled for core local functionality.
- Any external network service, update check, telemetry, analytics, or licensing communication MUST
  be explicitly documented, isolated behind dedicated adapter boundaries, and disclosed to the user.
- All persistence and external inputs MUST apply secure storage practices (such as Windows DPAPI or
  Credential Locker for secrets and least-privilege user directories) and strict input validation.

**Rationale**: Desktop utilities interacting with clipboard, notifications, and workspace context
sit inside a high-trust boundary; local-first isolation eliminates data exfiltration risks by
design.

### V. User Experience and Accessibility

- The user interface MUST remain consistent, responsive, and unobtrusive, preserving smooth frame
  pacing during transitions and never stealing focus unexpectedly from active applications.
- User interactions MUST prioritize simplicity, predictable hit targets, keyboard accessibility,
  and minimal visual complexity.
- Styling MUST use centralized theme tokens rather than hardcoded color values, and UI copy MUST
  maintain consistent casing and clear separation between interactive action verbs and passive
  status labels.
- The application MUST respect Windows accessibility and motion preferences, including High
  Contrast themes, text scaling, screen reader semantics, and Windows reduced-motion settings.
- Every widget MUST be independently configurable so users can enable, disable, or customize
  individual widgets without affecting overall application stability.

**Rationale**: An ambient desktop interface succeeds only when it feels effortless, respects
system-wide assistive settings, and stays out of the user's way until needed.

### VI. Engineering Discipline and Project Management

- Every feature MUST define measurable functional requirements, resource constraints, and testable
  acceptance criteria before implementation begins.
- Technical documentation, architecture diagrams, module contracts, and data schemas MUST be
  maintained in lockstep with the codebase.
- Technical risks, OS API constraints, and external dependencies MUST be identified and evaluated
  early during specification and planning.
- Development MUST be organized into logical, incrementally verifiable milestones.
- Automated and manual testing gates MUST pass before any feature or milestone is considered
  complete.
- Significant architectural decisions and engineering trade-offs MUST be recorded in concise
  Architectural Decision Records (ADRs).

**Rationale**: Disciplined requirements and early risk analysis prevent costly rework when building
system-integrated desktop software.

### VII. Specification-First and Evidence-Based Development (NON-NEGOTIABLE)

- Features MUST NOT be implemented without an approved specification (`spec.md`) and implementation
  plan (`plan.md`).
- Technologies, frameworks, and third-party packages MUST NOT be introduced without a documented
  evaluation of their suitability, resource footprint, Windows compatibility, and maintenance cost.
- Engineers and automated agents MUST NOT fabricate technical capabilities, silently stub required
  OS integrations, or assume undocumented Windows API behaviors.
- Every specification and plan MUST explicitly identify assumptions, platform limitations, edge
  cases, and unresolved questions before implementation proceeds.
- All engineering decisions MUST be evidence-based, backed by official platform documentation,
  empirical benchmarks, prototype validation, or automated tests.

**Rationale**: Grounding every change in approved specifications and empirical evidence prevents
speculative architecture, hidden platform bugs, and scope drift.

## Architectural & Operational Constraints

- **Layered Module Boundaries**: The codebase MUST maintain strict separation across four layers:
  1. **UI / Presentation**: Views, animations, theme tokens, and accessibility semantics.
  2. **Application / Domain**: Widget orchestration, state machines, configuration rules, and use
     cases.
  3. **Infrastructure / Persistence**: Local database/file storage, schema migrations, and
     serialization.
  4. **OS Integration Adapters**: Encapsulated Win32/WinRT/Windows API interop for windowing,
     display topology, power events, notifications, and media/system hooks.
- **Deterministic Fallbacks**: OS integration and persistence modules MUST degrade gracefully and
  recover deterministically when permissions are revoked, monitors disconnect, or local state files
  are corrupted.
- **Resource & Startup Budgets**: Implementation plans MUST define explicit targets for idle CPU
  (targeting 0% wake activity when idle/collapsed), working-set RAM, GPU memory, and cold-start
  readiness.

## Development Workflow & Quality Gates

- **Spec-Driven Lifecycle**: All work MUST follow the Spec Kit workflow:
  1. `/speckit-specify` to define user stories, edge cases, and measurable requirements.
  2. `/speckit-clarify` to resolve underspecified requirements and architectural ambiguities.
  3. `/speckit-plan` to evaluate technical suitability, architecture boundaries, and Constitution
     Check compliance.
  4. `/speckit-tasks` to organize work into dependency-ordered, independently testable milestones.
  5. `/speckit-implement` to execute tasks with automated verification gates.
- **Quality & Verification Gates**: Before a task or milestone is marked complete:
  - Type checking, static analysis, and compiler checks MUST pass with zero errors.
  - Automated unit and integration test suites MUST pass and cover core domain logic, data
    validation, and adapter contracts.
  - Multi-monitor DPI scaling, idle/battery resource suspension, and accessibility behaviors MUST be
    verified for any affected UI or OS integration component.
- **Repository Hygiene**: Commits MUST follow Conventional Commits, keep diffs focused on the
  approved task scope, and never commit machine-local IDE folders (`.idea/`, `.vscode/`) to the
  repository `.gitignore`.

## Governance

- **Authority**: This Constitution governs all specifications, architecture decisions,
  implementation plans, code reviews, and development tasks for the Shima project. In case of
  conflict between this Constitution and ad-hoc implementation choices, this Constitution prevails.
- **Amendment Process**: Amendments require:
  1. A documented proposal stating the change and its engineering rationale.
  2. An assessment of impact on existing specifications, plans, and architectural layers.
  3. Explicit user/architect approval and an updated version entry in this document.
- **Versioning Policy**: This Constitution uses Semantic Versioning (`MAJOR.MINOR.PATCH`):
  - **MAJOR**: Backward-incompatible governance changes, removal of a core principle, or relaxation
    of security, privacy, or specification-first mandates.
  - **MINOR**: Addition of a new principle or section, or materially expanded compliance gates.
  - **PATCH**: Editorial clarifications, wording refinements, or formatting fixes that do not alter
    governance semantics.
- **Compliance Review**: Every implementation plan (`Constitution Check` section) and code review
  MUST verify compliance with Principles I through VII. Any exception MUST be explicitly justified
  in the plan's `Complexity Tracking` table along with the reason simpler compliant alternatives
  were rejected.

**Version**: 1.0.0 | **Ratified**: 2026-11-21 | **Last Amended**: 2026-11-21
