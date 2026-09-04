# Template maintainer notes

This file exists only in the canonical starter and `scripts/init.sh` removes it from generated applications.

## Template tokens

- Application: `StarterKit` / `starterkit`
- Initial bounded context: `WorkTracking` / `worktracking`

Keep `.template-manifest` current when adding a token or template-only file. Test initialization from a Git archive or GitHub-generated repository, not by running the initializer in the canonical repository; it intentionally refuses that remote.

The sample vertical slice is retained after initialization because it teaches the intended dependency direction. A generated project can replace the sample only through domain scenarios and tests, not by deleting guardrails first.
