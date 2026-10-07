# Output Templates

The **Templates** tab turns a transcript into documents you define with prompts: DM
guidance, a running session summary, NPC/item/place lists, or similar notes. Four
starter templates are included; **Add starter templates** brings them back.

## Running a template

Templates that use the transcript run on the session being recorded, or otherwise on the
selected session. **Update now** runs once with the current inputs; **Stop** cancels.
**Keep updating when its inputs change** re-runs when new or corrected lines arrive (or a
template it uses produces new output), no more often than the template's **Every
(seconds)** setting. Nothing runs while the inputs are unchanged.

**Transcript characters** limits how much of a long transcript is sent (about 4
characters per token); lower it for small local models.

## Context and chaining

Each template picks its inputs: the **Transcript**, the **Reference files**, its
**Previous output** (it updates its last answer, so lists and summaries stay stable),
and the **Outputs of other templates**. A template without the transcript runs without
any session selected.

A template that uses other templates' outputs waits while any of them is updating, then
re-runs on their fresh results. Templates that would form a loop can't be selected. For
small local models, give each template one narrow job (for example only the turn order,
or only the open story beats) and combine them in a final template.

## Table screenshot

The shared **Table screenshot** card lets templates see your virtual tabletop. Pick your
Roll20 or Foundry browser window, or a whole screen, from **Capture** (a shortened title
such as `Roll20` matches any window containing it), set the **max width** (default
1280 px), and use **Test capture** to preview. Window capture works while other windows
cover it, but not while it is minimized.

Tick **Include a screenshot of the table** on a template to attach a fresh JPEG on each
update. Templates updating together share the same frame, and they also re-run when the
screen changes. This needs a vision model, for example Ollama `gemma4:e4b`. Screenshots
are kept in memory only and go to that template's LLM connection.

## Virtual tabletop assistant

**Add table (VTT) templates** adds eight linked templates, each with one narrow job so
small vision models can keep up. Screenshot readers:

- **Table: turn order** reads only the initiative tracker.
- **Table: token positions** reads only the battle map: tokens, PC or monster, where
  they are and who is next to whom.
- **Table: health and conditions** reads HP bars or numbers and status markers.
- **Table: dice rolls** reads the latest rolls from the chat log.
- **Table: scene and map** describes the map: place, lighting and fog, exits, terrain
  and hazards (every 2 minutes).

Text-only combiners:

- **Table: movement** lists who moved, appeared or disappeared since last time.
- **Table: combat log** keeps a round-by-round log from the turn order, rolls, health
  and table talk.
- **Table: DM reminders** combines turn order, movement, health, scene, the recent
  transcript and your reference files into up to 8 short reminders: whose turn is now
  and next, monster tactics, creatures low on HP or with conditions, rules to remember,
  and story beats or clues from your notes not presented yet.

Setup: pick your Roll20 (or Foundry) browser window in **Table screenshot**, use a vision
model such as Ollama `gemma4:e4b`, and point the **context folder** at your adventure PDF
and notes. All of them keep updating on their own; combiners re-run whenever one of their
inputs changes. Turn off readers you don't need to save GPU time.

Output is shown in the app and can be copied. **Also write the output to a file**
rewrites a `.md` or `.txt` file without locking it, so VS Code, Obsidian, or a browser
can keep it open.

## Reference files

Reference files are shared across templates unless unticked for a template. The model
can browse the chosen context folder with read-only list, search, and read tools for
PDF, DOCX, Markdown, text, JSON, CSV, and similar files.

The tools cannot reach outside the context folder. Always-included files are sent in
full with every update, about 60,000 characters total, so keep them short.

## LLM connections

Connections use the OpenAI-compatible `/v1/chat/completions` API. Built-in choices
include OpenRouter, NVIDIA Build, OpenAI, Ollama, LM Studio, and custom servers. Ollama
can point to `http://localhost:11434/v1` or another PC running Ollama with
`OLLAMA_HOST=0.0.0.0`.

The Ollama preset defaults to `gemma4:e4b` (reads text and images, supports tool
calling; install it with `ollama pull gemma4:e4b`).

**Load models** lists what the server offers. **Test** sends a one-line check and also
asks whether the model accepts images; **Check image support** runs just that check. API keys
are saved only when you choose to save them; saved keys are encrypted with Windows DPAPI
for your Windows account in `templates.json`.

Running a hosted template sends transcript text and any reference text the model reads
to that endpoint. Ollama and LM Studio keep text on your own machines.
