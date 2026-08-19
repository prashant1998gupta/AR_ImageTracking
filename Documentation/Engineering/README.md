# WebAR Engineering Handbook

> **Job:** teach a developer how to build WebAR on this stack - Unity WebGL, a
> commercial image-tracking plugin, a JavaScript overlay, and a PHP backend.
> What to do, what not to do, and why.
> **Update when:** an incident or a code review turns up a rule that is not
> written down here, or a rule here turns out to be wrong.
> **Do NOT put here:** anything specific to one campaign or client. Project
> architecture and features belong in that project's own documentation folder.

**This folder is portable.** It names no client and no campaign. Copy it into any
Unity WebGL + web-backend project and it works - there is no tooling to port,
only Markdown. Examples marked *"Seen in the field"* come from a real event
platform; they are illustrations, and every rule stands without them.

---

## I am about to... -> read this

Do not read this folder front to back. Find what you are about to do.

| I am about to... | Read |
|---|---|
| Work on this stack for the first time | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) - all of it, once |
| Trust a Unity build-time callback to inject something | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Build-time callbacks |
| Position, scale or parent anything on an image target | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § The plugin owns your transform |
| Add video or audio to a target | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Streaming and page load |
| Lay out a HUD row, chip list or name column | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Layout and measurement |
| Measure an element to fit text | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Layout and measurement |
| Ship an asset through a CDN | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Caching |
| Trust anything a phone tells the server | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Client-asserted data |
| Write an error message for a failed lookup | [WEBAR-TRAPS.md](WEBAR-TRAPS.md) § Client-asserted data |

---

## How rules are written

Every rule has the same shape, so you can scan it:

> ### The rule, as an instruction
> `breaks things` · `junior trap`
>
> **Why** - the mechanism. What actually happens at runtime. A rule without a
> mechanism gets cargo-culted or ignored; neither is learning.
>
> Wrong code, then right code.
>
> **Spot it** - how a reviewer or a tool finds this.

Severity means:

- `breaks things` - causes a real defect. Not negotiable.
- `costly` - works, but wastes performance, money or time. Fix when you touch the file.
- `style` - consistency only. Follow it, do not argue about it.

`junior trap` marks the ones that are genuinely counter-intuitive. Nobody is
stupid for hitting them; they are traps because the code looks correct.

---

## The four ideas behind all of it

If you remember nothing else:

**1. Silence is the enemy - and a silent default is worse than a crash.** The
worst defects on this stack throw nothing and print nothing. A build-time
callback that did not run, a plugin that overwrote your transform, a flex child
that clipped a name, a scan that was never counted. Every one of them looked
fine. So: prefer designs that fail *loudly*, and never let a step that did not
happen fall back to a plausible-looking value.

*Seen in the field: a skipped Unity callback defaulted the campaign flag to
"off". The build looked perfect and shipped to a live event with no registration
screen at all. Nothing errored. It stayed broken across every later build.*

**2. You do not own what a vendor package touches every frame.** A tracking
plugin that writes your transform each frame will silently undo your scale, your
position, and your careful work. Find what the package writes, and put your own
values somewhere it does not reach.

**3. The server owns the rules; the build owns the experience.** Anything you
might want to change during a live event - text, scoring, availability, branding
- must be data the server sends, not something compiled into a build. A rebuild
during an event is a failure of design, not a task.

**4. Treat every phone as an unreliable narrator.** Client-asserted events can be
forged, replayed, delayed by hours on venue wifi, or arrive from a browser you
never tested. Validate on the server, make the offline path explicit, and assume
anything visible to the player is visible to an attacker.

---

## Local conventions

Anything a specific project adds to these rules lives in that project's own docs,
not here. If a project's rule contradicts a rule in this folder, the project's
rule wins **and should say why** - that is a real trade-off worth writing down,
not an exception to hide.
