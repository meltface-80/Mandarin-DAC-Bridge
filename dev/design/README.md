# Designs

A feature's design lives here, one file per feature (`<feature>.md`), and is the feature's
specification (`CLAUDE.md`, *Spec authority*): the code matches it, or the design changes first, in
the same commit as the code. A question a design leaves open goes to the owner before the code that
depends on it is written.

The features built before the first design are described by the README (what the owner and the
apps see), the CHANGELOG (why each change was made) and the comments at the top of each source file
(how it works). A new feature that needs decisions the owner hasn't made yet starts with its design
here.

| Design | Status |
|---|---|
| [`password.md`](password.md) | Draft: open questions for the owner |
| [`updater.md`](updater.md) | Draft: open questions for the owner |
