# Issue tracker: Local Markdown

Issues and specs live as Markdown files in `.scratch/`.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`.
- Spec: `.scratch/<feature-slug>/spec.md`.
- One file per implementation ticket:
  `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01`.
- Record triage state in a `Status:` line near the top of each issue.
  Use the role strings in `triage-labels.md`.
- Append comments under a `## Comments` heading.

## Publishing and fetching

To publish, create the appropriate spec or ticket file using these conventions.
Create directories as needed.

To fetch a ticket, read its referenced path. Resolve a ticket number within
the relevant feature directory.

## Wayfinding operations

- Map: `.scratch/<effort>/map.md`, with Notes, Decisions-so-far, and Fog.
- Child ticket: `.scratch/<effort>/issues/NN-<slug>.md`.
- Record ticket type in `Type:`: research, prototype, grilling, or task.
- Record dependencies in `Blocked by: NN, NN`.
- A ticket is unblocked when all its dependencies are resolved.
- Frontier: select the lowest-numbered open, unblocked, unclaimed ticket.
- Claim: set `Status: claimed` and save before working.
- Resolve: append an `## Answer`, set `Status: resolved`, then add
  a summary and ticket link to Decisions-so-far in the map.

Wayfinding claim/resolution states are separate from triage classification.
