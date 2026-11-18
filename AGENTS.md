# Repository instructions

## Bug fixes

- Every bug fix must include an associated regression test in the same change.
- The test must reproduce the bug and verify the corrected behavior. Where
  practical, confirm that it fails before the fix and passes afterward.
- Run the associated test and relevant existing tests before completing the fix.
  If execution is not possible, report the limitation explicitly.

## Test naming

- Separate words in test method names with underscores, for example
  `Rejects_Invalid_Arguments`. Use the same convention for explicit test case names.
- For Boolean variations, use `[Test]` and `[Values]` on the Boolean parameter
  instead of separate `[TestCase(false)]` and `[TestCase(true)]` attributes.

## Code style

- Qualify access to non-static fields, properties, methods and events with `this.`.
- Put a blank line between property and method declarations.
- Put a blank line after a multiline expression statement or declaration before
  subsequent code.
- Put a blank line after a closing code-block brace before subsequent code,
  except before `else` (including `else if`) and between the `try`, `catch` and
  `finally` clauses of the same construct.
- Put braces for `try`, `catch` and `finally` blocks on their own lines.
- Use the appropriate modern `ThrowIf...` guard methods instead of equivalent
  manual argument checks, for example `ArgumentOutOfRangeException.ThrowIfNegative(maxBytes)`.
- For calls with more than three arguments, put each argument on its own line,
  indented one level beyond the call.
- Put a blank line between a namespace declaration and the file's contents.
- Use statement bodies for long expression-bodied methods; use 120 characters
  as the line-length guideline.
- Short `if (...) throw ...;` guards may stay on one line. Put a blank line after
  the guard, or after the final guard in a consecutive group.
- Enclose the `throw` body of a multiline `if` guard in braces, including when
  it contains only that single statement.
- Keep one class per file; extract helper classes into their own files.
- Format auto-properties on one line, for example `public required bool SyncAllowed { get; init; }`.
- Give protocol values, keys, paths, offsets and limits descriptive constants or enums; avoid magic numbers and strings.
- Use `string.Empty` instead of empty string literals.
- Use injected `TextWriter` instances for output and diagnostics. Only the
  application entry point selects `System.Console.Out` and `System.Console.Error`;
  pass them through callers/DI instead of accessing the global console in services.
- Always enclose `for`, `foreach` and `while` loop bodies in braces, including single-statement bodies.

## Dependency licenses

- When adding or updating a dependency, retain its required license text,
  copyright notices and attribution in the repository's license/NOTICE area.
- Document the use of LGPL licensed dependencies in `THIRD-PARTY-NOTICES.md`; include the complete, unmodified license text and the upstream authors' copyright notice under `Licenses/`.
- Ship the license texts and notices with build and publish output, not only as
  external links. Keep the original notices when updating a package.
- Record the corresponding upstream source/version and whether the dependency
  has been modified.

## Project layout

- Keep the application and core code together in `Source/NoTunes/NoTunes.csproj`.
- Give each project a separate directory that does not contain other projects.
- Group the platform projects in the `Platform` solution folder.
