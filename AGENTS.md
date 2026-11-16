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

## Code style

- Qualify access to non-static fields, properties, methods and events with `this.`.
- Put a blank line between property and method declarations.
- Put a blank line after a closing code-block brace before subsequent code,
  except between the `try`, `catch` and `finally` clauses of the same construct.
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
- Keep one class per file; extract helper classes into their own files.
- Format auto-properties on one line, for example `public required bool SyncAllowed { get; init; }`.

## Project layout

- Keep the application and core code together in `Source/NoTunes/NoTunes.csproj`.
- Give each project a separate directory that does not contain other projects.
- Group the platform projects in the `Platform` solution folder.
