# DDD review checklist

- Which bounded context owns this change?
- Which concrete business scenario or invariant changed?
- Does the code use the ubiquitous language in `GLOSSARY.md`?
- Did any HTTP, serialization, database, messaging, or framework type leak into Domain?
- Are external time, identifiers, randomness, and facts explicit inputs?
- Is a new abstraction solving a current problem?
- Are private domain types reconstructed through validated functions and explicit mappings?
- Does Application depend only on Domain?
- Are concrete adapters selected only in Web?
- Is CQRS, event sourcing, messaging, mediation, or a microservice being introduced? If so, where is the demonstrated need and ADR when required?
- Do domain, application, integration, web, property, and architecture tests prove the intended behavior and dependency direction?
