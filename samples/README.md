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
