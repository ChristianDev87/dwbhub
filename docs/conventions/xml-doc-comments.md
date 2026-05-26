# C# XML doc comments

Short notes on how we document C# code in this project.

## The split

`///` XML doc goes directly above a declaration and describes the contract — what it does, what it returns, what it can throw. `//` plain comments live inside a method body and describe the *why* — the constraint, the rationale, the upstream bug. If a comment is sitting above a declaration and reads like part of the contract, it should be `///`, not `//`.

## When to add XML doc

Public types, methods, properties, events, and non-trivial constructors. Same for `internal` symbols that cross an assembly boundary via `InternalsVisibleTo`. Anything else is optional — add it for `internal` and `private` when the name alone doesn't tell you what's happening. Tests don't need it; the method name is the spec.

## Tags we use

`<summary>` on everything we document. One to three sentences, present tense, ends with a period.

`<param>` when the parameter isn't obvious from its name and type. Cancellation tokens, IDs, dictionaries with a known key type — usually obvious. Free-form strings or numeric inputs with a particular range — usually not.

`<returns>` when the shape needs explaining. `null` semantics, sentinel values, "first match wins" — yes. Returning a `Task<List<X>>` — no, the type already says that.

`<exception cref="X">` for every exception we explicitly throw, including rethrows.

`<remarks>` for longer context — security notes, "why this exists" prose, version-specific behaviour. Goes after `<summary>`.

`<see cref="X"/>` and `<paramref name="x"/>` for cross-references inside prose. `<c>code</c>` for inline identifiers.

`<inheritdoc/>` on interface implementations. The interface carries the contract; the implementation just inherits it. If the implementation has something callers should know about, add a `<remarks>` after the `<inheritdoc/>` for that specific note — don't paraphrase the interface doc.

We don't use `<value>` (covered by `<summary>` on properties) or `<typeparam>` (only worth it when the type parameter constraint is semantically interesting). An empty `<summary />` just adds noise — if there's nothing useful to say, skip it.

## Style

English in XML doc. Imperative present tense — "Hash the password", not "Hashes the password" or "Will hash the password". Don't restate the type system, don't restate the method name. A `<summary>` like `Save the user` for `Save(User user)` is just noise.

Short summaries can stay on one line: `/// <summary>Look up a tenant by its URL slug.</summary>`. Longer ones use the three-line block.

## A few special cases

**Positional records.** Document each component as `<param>` on the record declaration itself, alongside the `<summary>`. That's the one place where `<param>` shows up outside a method signature.

**Interface implementations.** `<inheritdoc/>` on every public method. The class itself still needs its own `<summary>` describing what flavour of the contract it implements — e.g. "Dapper-backed implementation of …" or "AES-GCM-backed implementation of …".

**Migrations.** A class-level `<summary>` saying what the migration adds is enough. `Up()` and `Down()` are framework-convention; they don't need doc.

**Controllers.** Each endpoint action gets a `<summary>` covering the user-facing contract — auth requirements, response shape, error semantics. `<param>` for non-obvious route or query params. Skip `<exception>` here; controllers return `IActionResult`, they don't throw.

**Framework entrypoints.** Methods like ASP.NET middleware `InvokeAsync` or custom auth filters' `Authorize` can stay undocumented when the class-level `<summary>` already covers the contract.

**Tests.** Test methods stay bare — the test name is the spec. Shared test fixtures or helpers used across projects do need `<summary>`. `// Arrange / // Act / // Assert` and case labels for `Theory` data are fine as inline `//`.

## What stays as `//`

These belong inside a method body, never on a declaration:

- Order constraints between statements, e.g. "this has to run before that because …"
- Security rationales at the point of decision
- REST endpoint labels above the HTTP call so a reviewer sees which URL the block is talking about
- `TODO:` / `FIXME:` / `HACK:` markers
- Tool directives like `// ReSharper disable …` or `// pragma …`
- Project-specific linter markers — the SQL tenant-filter check, for one, greps for inline comment patterns; treat anything that looks like `// SOMETHING:` as load-bearing until you've checked
- Section headers in long files (`// ── Public API ──`) — useful navigation, not contract
- `// Arrange / // Act / // Assert` in tests

What doesn't belong here: comments directly above a public/internal declaration that describe the declaration (those are `///`), release tags and sprint references that go stale, and one-liners that just restate the next statement.

## Tooling

Per-project the csproj can opt into compile-time enforcement once the project's public API is fully documented:

```xml
<PropertyGroup>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <!-- Remove CS1591 from NoWarn to turn missing-doc into a warning. -->
</PropertyGroup>
```

CS1591 ("Missing XML comment for publicly visible type or member") then catches gaps at build time.
