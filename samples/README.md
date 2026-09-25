# Samples

Open a sample with **Open** (`Ctrl+O`) and press **Run Once** (`Ctrl+R`). Nodes flash in the order they
run: green when they succeed, red when they fail. Skipped nodes do not flash; they are dimmed until
the next run.

Samples 01–04 show **Run If** and **Done**, the ports on nodes that touch the outside world (files,
HTTP, processes):

- Wiring one node's **Done** into another's **Run If** means "after that".
- Any boolean wired into **Run If** is a condition: the node runs only when it is true.
- A node that is not run is **skipped**, and so is everything that takes data from it, or from a node
  that failed.
- **Select**, **Coalesce**, **Is Empty** and **Output** merge branches, so they still run and read a
  skipped input as empty.

The samples write their files to your temp folder (`%TEMP%`), building the path with Environment
Variable → Combine, so they work on any machine.

## 01 — Write, then read

`01-write-then-read.vnoder`

File Write saves the current time to a file, and File Read reads it back. Without a wire between them
the two could run in either order, and the read could see the previous file. **File Write's Done →
File Read's Run If** makes the read run second. Run it a few times: Output always shows the time just
written.

## 02 — Download once, then use the cache

`02-download-once-then-cache.vnoder` (needs internet)

An if / else built from two gated branches:

- **Path Exists** checks for the cache file.
- *Not cached*: **Not** → HTTP Request's Run If downloads `https://example.com`, and File Write saves
  it. File Write takes the request's Body, so it is skipped whenever the request is.
- *Cached*: **Exists** → File Read's Run If reads the saved copy.
- **Coalesce** joins the branches, taking whichever one ran, and Regex Extract All pulls out the page
  title.

First run: File Read is skipped (dimmed) and the top-left Output shows `false`. Run again: now HTTP
Request and File Write are skipped, and the page comes from the cache. Delete
`%TEMP%\vnoder-sample-02-cache.html` to start over.

## 03 — A failure stops what depends on it

`03-failure-stops-downstream.vnoder`

File Read points at a file that does not exist, so it fails (red). To Upper and Length take data from
it, so they are **skipped**, instead of running on an empty or stale value. The Output on the right
merges, so it still runs and shows nothing. Coalesce also merges: its A input is skipped, so it falls
back to the Static Text on B.

## 04 — A condition gates a write

`04-condition-gates-a-write.vnoder` — press **Continuous** (`Ctrl+Shift+R`); the interval is 1000 ms.

The top row works out whether the current second is even (seconds → To Number → Modulo 2 → Equals 0),
and feeds that into File Write's Run If. File Write appends a line to
`%TEMP%\vnoder-sample-04-log.txt` only on even seconds. File Read reads the log back after the write
(Done → Run If), and the Outputs show the last line and how many lines there are.

Let it run: on odd seconds the write and everything after it dim out, and on even ones the count
goes up. Stop it with **Stop**.

## 05–10 — Showcases: every node at work

Six larger samples, each a set of small, real tasks. Every task sits in its own named group box, so
collapse the ones you are not reading (double-click a header). Between them (with 01–04 and the rest)
the samples use every node type. Press **Run Once** and read the Outputs; values are not saved in the
file, so the Outputs start empty.

### 05 — Site monitor

`05-site-monitor.vnoder` (needs internet) — press **Continuous** (`Ctrl+Shift+R`, every 5 s).

Pings `example.com`, checks port 443, its TLS certificate (expiry, days left, a "renew soon" check)
and DNS, fetches the page and reads its title. When the page **changes** (Hash → Changed → Run If) it
saves a snapshot to `%TEMP%\vnoder-showcase` and shows a notification; on the first run that is
"snapshot saved", afterwards only on a change. Log, Plot, History, Counter and Previous Value fill up
as the run goes on. Send Email is wired but switched off: fill in the SMTP fields and tick its Boolean.

### 06 — Data wrangling

`06-data-wrangling.vnoder`

A small sales CSV as a table, totals and revenue (price × qty, run once per row), distinct regions as
JSON, the same rows as semicolon CSV and TSV, settings merged from defaults, stamped and shown as YAML
and pretty JSON, and an RSS feed read with XPath: batches, a random headline, a search, filters and
slices, and a link taken apart.

### 07 — Text tools

`07-text-tools.vnoder`

Slugs and code-style names from a Czech title, a diff of two versions of release notes, Markdown to
HTML, similarity, a palindrome check, trimming and padding an invoice number, regex extraction and
email redaction, and round trips through HTML, URL, Base64, Hex, Brotli and the Windows-1250 code page.

### 08 — Security toolkit

`08-security-toolkit.vnoder`

A private CA and a certificate it issues, a CSR, a message signed and a tampered copy rejected, a JWT
made and decoded, 2FA codes with the setup QR code read back, a webhook HMAC, AES, BCrypt and PBKDF2,
and an API key from a GUID and a random string.

### 09 — Numbers and dates

`09-numbers-and-dates.vnoder`

A sine wave (Range run per item through Map Range and Sin, into Plot), a circle, a 3-4-5 triangle
solved four ways, compound interest, a thermostat, paging, logic puzzles (a coin, two-way light
switches, a door alarm, a weekend check), and dates: parts, month end, time zones, cron, Unix time.
The statistics of Random Gaussian samples settle as **Continuous** collects more.

### 10 — Files, images and the system

`10-files-images-system.vnoder` — writes to `%TEMP%\vnoder-showcase`.

A banner drawn and saved as PNG, grey PNG and a flipped WebP thumbnail, an HTML report with the image
embedded, a backup copied and moved, then (after a Delay) the folder zipped, hashed and unzipped into a
cleaned folder, all sequenced with Done → Run If. Also: drive space, machine and OS details, processes,
an environment variable and a program's output. Process Kill (Notepad) and Clipboard Set are wired but
switched off by their Booleans.

Delete `%TEMP%\vnoder-showcase*` to clean up after 05 and 10.
