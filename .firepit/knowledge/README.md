# Knowledge

Project knowledge base — research notes, background docs, decisions.

Conventions:

- One markdown file per topic, written in English (the search index
  embeds English best).
- These files are committed — they are part of the project.
- `knowledge.db` next to this folder is the derived search index:
  gitignored, rebuilt automatically from the markdown at any time.
- Search and add via the Firepit MCP tools `firepit_knowledge_search`
  and `firepit_knowledge_add`; correct or retire stale docs with
  `firepit_knowledge_update` / `firepit_knowledge_delete`.
- Docs whose frontmatter says `pin: true` are the always-on tier:
  Firepit compiles them into `.firepit/knowledge-pinned.md`, which the
  CLAUDE.md convention auto-loads into every session. Keep that set
  small — reflex rules only.
