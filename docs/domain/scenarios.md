# WorkTracking domain scenarios

These examples use the ubiquitous language in `CONTEXT.md`. They are the source material for domain and application tests.

## Create an open work item

- **Given** a person supplies the title `Review the context`.
- **When** the work item is created with a new identifier at 10:00 UTC.
- **Then** the title is normalized, the identifier and creation time are retained, and the work item is open.

## Reject a blank title

- **Given** a person supplies a title containing only whitespace.
- **When** creation is attempted.
- **Then** creation is rejected as invalid and no work item is stored.

## Reject an overlong title

- **Given** a title contains more than 100 characters.
- **When** creation is attempted.
- **Then** creation is rejected as invalid and no work item is stored.

## Complete open work

- **Given** an open work item created at 10:00 UTC.
- **When** completion occurs at 10:15 UTC.
- **Then** the same work item is completed at 10:15 UTC and its title is unchanged.

## Reject repeated completion

- **Given** a work item completed at 10:15 UTC.
- **When** completion is attempted again at 10:16 UTC.
- **Then** the attempt is rejected because the work item is already completed, and no state is stored.

## Reject completion before creation

- **Given** a work item created at 10:00 UTC.
- **When** completion is attempted with a time before 10:00 UTC.
- **Then** the attempt is rejected and no state is stored.

## Invariants

- A work item identifier is never empty.
- A title is trimmed, contains at least one non-whitespace character, and is at most 100 characters.
- A work item is either open or completed at one specific time.
- Completion cannot occur before creation.
- A completed work item cannot be completed again.
