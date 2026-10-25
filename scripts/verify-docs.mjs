#!/usr/bin/env node
/**
 * 知识库守卫：检查决策笔记结构、生成物是否过期、相对链接是否可点、AGENTS.md 体积。
 *
 *   node scripts/verify-docs.mjs           只检查，发现问题退出码 1
 *   node scripts/verify-docs.mjs --write   重写 .agents/docs/generated/ 下的生成物后再检查
 *
 * 零依赖，只需要 Node 20+。
 */
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";

const ROOT = resolve(process.env.DOCS_ROOT ?? process.cwd());
const WRITE = process.argv.includes("--write");
const errors = [];

const ABS = (rel) => join(ROOT, rel);
const relOf = (abs) => relative(ROOT, abs).split(sep).join("/");
const read = (rel) => readFileSync(ABS(rel), "utf8").replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n");

/** 递归列出某目录下的文件；跳过隐藏目录与构建产物。 */
function walk(dir, out = []) {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith(".") || entry.name === "node_modules" || entry.name === "bin" || entry.name === "obj" || entry.name === "Output" || entry.name === "TestResults") continue;
    const abs = join(dir, entry.name);
    if (entry.isDirectory()) walk(abs, out);
    else out.push(abs);
  }
  return out;
}

// ---------------------------------------------------------------- 笔记结构

const NOTE_ROOT = ".agents/notes";
const LIFECYCLES = ["proposed", "implemented", "rejected"];
const ARCHIVE = "archived";
const CLASSES = ["feature", "bug-fix", "simplification", "architecture", "process", "testing"];
const STATUS = {
  proposed: /^Status: proposed$/,
  implemented: /^Status: implemented$/,
  rejected: /^Status: rejected — .+$/,
};
const REQUIRED = {
  proposed: [["## Proposal", "## 提议", "## 方案", "## 提案"], ["## Acceptance criteria", "## 验收标准", "## 验收条件"], ["## Risks", "## 风险"]],
  implemented: [["## Decision", "## 决定", "## 决策"], ["## Consequences", "## 后果", "## 影响", "## 结果"]],
  rejected: [["## Proposal", "## 提议", "## 方案", "## 提案"]],
};
const BANNED_IN_IMPLEMENTED = new Set(["## proposal", "## plan", "## migration plan", "## acceptance criteria", "## 提议", "## 方案", "## 提案", "## 计划", "## 规划", "## 迁移计划", "## 验收标准", "## 验收条件"]);
const ALTERNATIVES = /^## (?:Alternatives considered|.{0,8}?(?:替代方案|备选方案))$/;
const headingBase = (h) => h.replace(/[（(].*$/, "").trimEnd();

/** 把围栏代码块与 HTML 注释遮住，避免里面的 # 被当成标题。 */
function maskLines(text) {
  const lines = text.split("\n");
  const prose = new Array(lines.length).fill(true);
  let fence = false;
  let comment = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const opensFence = /^\s{0,3}```/.test(line);
    if (!comment && opensFence) {
      prose[i] = false;
      fence = !fence;
      continue;
    }
    if (fence) {
      prose[i] = false;
      continue;
    }
    if (comment) {
      prose[i] = false;
      if (line.includes("-->")) comment = false;
      continue;
    }
    const open = line.indexOf("<!--");
    if (open !== -1) {
      prose[i] = false;
      const close = line.indexOf("-->", open + 4);
      if (close === -1) comment = true;
    }
  }
  return { lines, prose };
}

const notes = [];
if (existsSync(ABS(NOTE_ROOT))) {
  for (const entry of readdirSync(ABS(NOTE_ROOT), { withFileTypes: true })) {
    if (entry.isDirectory() && entry.name !== ARCHIVE && !LIFECYCLES.includes(entry.name)) {
      errors.push(`笔记结构: ${NOTE_ROOT}/${entry.name}/ 不是合法的生命周期目录（允许 ${LIFECYCLES.join(" / ")}，另有 ${ARCHIVE}/）`);
    }
    if (entry.isFile() && entry.name === "INDEX.md") {
      errors.push(`笔记结构: ${NOTE_ROOT}/INDEX.md 不允许手写；索引由 scripts/verify-docs.mjs 生成到 .agents/docs/generated/notes-index.md`);
    }
  }

  for (const file of walk(ABS(NOTE_ROOT))) {
    if (!file.endsWith(".md")) continue;
    const rel = relOf(file);
    const segments = rel.slice(`${NOTE_ROOT}/`.length).split("/");
    if (segments.length !== 3) {
      errors.push(`笔记结构: ${rel} 必须是 ${NOTE_ROOT}/{生命周期}/{分类}/yyyy-mm-dd-主题.md`);
      continue;
    }
    const [lifecycle, cls, base] = segments;
    if (!LIFECYCLES.includes(lifecycle) && lifecycle !== ARCHIVE) {
      errors.push(`笔记结构: ${rel} 的生命周期目录 "${lifecycle}" 非法`);
      continue;
    }
    if (!CLASSES.includes(cls)) {
      errors.push(`笔记结构: ${rel} 的分类目录 "${cls}" 非法（允许 ${CLASSES.join(" / ")}）`);
      continue;
    }
    if (!/^\d{4}-\d{2}-\d{2}-.+\.md$/.test(base)) {
      errors.push(`笔记结构: ${rel} 文件名必须是 yyyy-mm-dd-主题.md`);
      continue;
    }

    const text = read(rel);
    const { lines, prose } = maskLines(text);
    const fail = (msg) => errors.push(`笔记格式: ${rel} — ${msg}`);

    if (!/^# Agent Note[:：] \S/.test(lines[0] ?? "")) fail("第 1 行必须是 `# Agent Note: 标题`");
    if ((lines[1] ?? "").trim() !== "") fail("第 2 行必须是空行");
    const wantStatus = STATUS[lifecycle];
    if (wantStatus && !wantStatus.test(lines[2] ?? "")) fail(`第 3 行状态必须匹配 ${wantStatus}`);
    if ((lines[3] ?? "").trim() !== "") fail("第 4 行必须是空行");

    const statusHits = prose.filter((_, i) => /^Status: (proposed|implemented|rejected)/.test(lines[i].trim())).length;
    if (statusHits !== 1) fail("Status 行必须且只能出现一次");

    const headings = lines.filter((_, i) => prose[i] && lines[i].startsWith("## ")).map((h) => headingBase(h.trimEnd()));
    if (!["## Problem", "## 问题"].includes(headings[0] ?? "")) fail("第一节必须是 `## Problem`");
    for (const group of REQUIRED[lifecycle] ?? []) {
      if (!group.some((h) => headings.includes(h))) fail(`缺少章节 ${group.join(" 或 ")}`);
    }
    if (lifecycle === "implemented") {
      for (const h of headings.filter((x) => BANNED_IN_IMPLEMENTED.has(x.toLowerCase()))) {
        fail(`已实施笔记不允许保留提案期章节 ${h}`);
      }
    }
    if (!headings.some((h) => ALTERNATIVES.test(h))) fail("缺少 `## Alternatives considered`（至少写一条真实考虑过的备选方案）");

    if (lifecycle !== ARCHIVE) notes.push({ lifecycle, cls, base, rel, date: base.slice(0, 10), title: (lines[0] ?? "").replace(/^# Agent Note[:：]\s*/, "").trim(), problem: firstProblemLine(lines, prose) });
  }

  // 归档笔记不可改写：manifest.json 里记下每个文件的 sha256。
  const archived = walk(ABS(join(NOTE_ROOT, ARCHIVE))).filter((f) => f.endsWith(".md"));
  if (archived.length) {
    const manifestPath = join(NOTE_ROOT, ARCHIVE, "manifest.json");
    if (!existsSync(ABS(manifestPath))) {
      errors.push(`笔记归档: 缺少 ${manifestPath}；运行 node scripts/verify-docs.mjs --write 生成`);
    } else {
      const manifest = JSON.parse(read(manifestPath));
      for (const file of archived) {
        const rel = relOf(file).slice(`${NOTE_ROOT}/${ARCHIVE}/`.length);
        const digest = "sha256:" + createHash("sha256").update(readFileSync(file)).digest("hex");
        if (manifest.files?.[rel] !== digest) errors.push(`笔记归档: ${rel} 与 manifest.json 记录不一致；归档笔记禁止改写`);
      }
    }
  }
}

function firstProblemLine(lines, prose) {
  const start = lines.findIndex((l, i) => prose[i] && /^## (Problem|问题)$/.test(l.trim()));
  if (start === -1) return "";
  for (let i = start + 1; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!line) continue;
    if (line.startsWith("#")) return "";
    const plain = line.replace(/[*_`\[\]]/g, "").replace(/\s+/g, " ");
    const end = plain.search(/[。；]/);
    if (end !== -1) return plain.slice(0, end + 1);
    return plain.length > 60 ? plain.slice(0, 60) + "…" : plain;
  }
  return "";
}

// ---------------------------------------------------------------- 生成物

function buildNotesIndex() {
  const order = ["proposed", "implemented", "rejected"];
  const sorted = [...notes].sort((a, b) => {
    const byLifecycle = order.indexOf(a.lifecycle) - order.indexOf(b.lifecycle);
    return byLifecycle !== 0 ? byLifecycle : b.date.localeCompare(a.date);
  });
  const rows = sorted.map((n) => {
    const link = `[${n.title}](${relative(ABS(".agents/docs/generated"), ABS(n.rel)).split(sep).join("/")})`;
    return `| ${n.date} | ${n.lifecycle} | ${n.cls} | ${link} | ${n.problem.replace(/\|/g, "\\|")} |`;
  });
  return [
    "# 决策笔记索引",
    "",
    "> 自动生成，勿手改。重新生成：`node scripts/verify-docs.mjs --write`。",
    "",
    `共 ${sorted.length} 篇。按状态排序：提案 → 已实施 → 已否决。`,
    "",
    "| 日期 | 状态 | 分类 | 标题 | 问题 |",
    "|---|---|---|---|---|",
    ...rows,
    "",
  ].join("\n");
}

function buildPaths() {
  const projects = walk(ABS(".")).filter((f) => f.endsWith(".csproj") || f.endsWith(".sln")).map(relOf).sort();
  const schemas = walk(ABS(".")).filter((f) => f.endsWith(".xsd")).map(relOf).sort();
  const sources = walk(ABS("Ra3.BattleNet.Metadata")).filter((f) => f.endsWith(".cs")).map((f) => ({ rel: relOf(f), lines: readFileSync(f, "utf8").split("\n").length })).sort((a, b) => a.rel.localeCompare(b.rel));
  const tests = walk(ABS("Ra3.BattleNet.Metadata.Tests")).filter((f) => f.endsWith(".cs")).map(relOf).sort();
  const dataDirs = readdirSync(ABS("Metadata"), { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name).sort();
  const entityDirs = (kind) => readdirSync(ABS(`Metadata/${kind}`), { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name).sort();

  const rows = (list, label) => list.map((x) => (typeof x === "string" ? `| \`${x}\` | ${label} |` : `| \`${x.rel}\` | ${x.lines} |`));

  return [
    "# 仓库路径地图",
    "",
    "> 自动生成，勿手改。重新生成：`node scripts/verify-docs.mjs --write`。行数随代码变化，路径以本表为准。",
    "",
    "## 项目与解决方案",
    "",
    "| 路径 | 说明 |",
    "|---|---|",
    ...rows(projects, "项目 / 解决方案"),
    "",
    "## XSD 模式",
    "",
    "| 路径 | 说明 |",
    "|---|---|",
    ...rows(schemas, "XML 模式"),
    "",
    "## 核心解析库源码",
    "",
    "| 文件 | 行数 |",
    "|---|---|",
    ...rows(sources, ""),
    "",
    "## 测试源码",
    "",
    "| 文件 | 说明 |",
    "|---|---|",
    ...rows(tests, "MSTest 测试"),
    "",
    "## 原始数据树 `Metadata/`",
    "",
    "| 目录 | 内容 |",
    "|---|---|",
    ...dataDirs.map((d) => `| \`Metadata/${d}/\` | 一级目录 |`),
    "",
    `Mod 实体目录（${entityDirs("mods").length} 个）：${entityDirs("mods").map((d) => `\`${d}\``).join("、")}`,
    "",
    `Application 实体目录（${entityDirs("apps").length} 个）：${entityDirs("apps").map((d) => `\`${d}\``).join("、")}`,
    "",
  ].join("\n");
}

const GENERATED = {
  ".agents/docs/generated/notes-index.md": buildNotesIndex(),
  ".agents/docs/generated/paths.md": buildPaths(),
};

for (const [rel, expected] of Object.entries(GENERATED)) {
  const actual = existsSync(ABS(rel)) ? read(rel) : null;
  if (WRITE) {
    if (actual !== expected) {
      mkdirSync(dirname(ABS(rel)), { recursive: true });
      writeFileSync(ABS(rel), expected, "utf8");
    }
    continue;
  }
  if (actual === null) errors.push(`生成物缺失: ${rel}；运行 node scripts/verify-docs.mjs --write`);
  else if (actual !== expected) errors.push(`生成物过期: ${rel} 与仓库实际内容不一致；运行 node scripts/verify-docs.mjs --write`);
}

// ---------------------------------------------------------------- 链接与体积

const LINK = /\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
const DOC_FILES = ["README.md", "AGENTS.md", ...walk(ABS(".agents")).map(relOf)];

for (const rel of DOC_FILES) {
  if (!rel.endsWith(".md")) continue;
  const text = read(rel);
  let match;
  LINK.lastIndex = 0;
  while ((match = LINK.exec(text)) !== null) {
    let target = match[1];
    if (/^(https?:|mailto:|#)/.test(target) || target.includes("<")) continue;
    target = decodeURIComponent(target.split("#")[0]);
    if (!target) continue;
    if (!existsSync(resolve(ROOT, dirname(rel), target))) {
      errors.push(`链接失效: ${rel} → ${match[1]}`);
    }
  }
}

const agentsLines = read("AGENTS.md").split("\n").filter((l) => l.trim()).length;
if (agentsLines > 130) errors.push(`AGENTS.md 有 ${agentsLines} 行正文，超过 130 行预算；把细节移进 .agents/docs/ 并在 AGENTS.md 里只留指针`);

// 孤儿文档：入口文件点不到的知识库文件等于不存在。
const entry = ["README.md", "AGENTS.md"];
const seen = new Set(entry);
const queue = [...entry];
while (queue.length) {
  const rel = queue.pop();
  const text = read(rel);
  let match;
  LINK.lastIndex = 0;
  while ((match = LINK.exec(text)) !== null) {
    let target = match[1];
    if (/^(https?:|mailto:|#)/.test(target)) continue;
    target = decodeURIComponent(target.split("#")[0]);
    if (!target.endsWith(".md")) continue;
    const abs = resolve(ROOT, dirname(rel), target);
    if (!existsSync(abs)) continue;
    const next = relOf(abs);
    if (seen.has(next)) continue;
    seen.add(next);
    queue.push(next);
  }
}
for (const rel of DOC_FILES) {
  if (rel.endsWith(".md") && !seen.has(rel)) errors.push(`文档孤立: ${rel} 没有被任何入口文件链到；在 .agents/docs/index.md 里加一条`);
}

// ---------------------------------------------------------------- 结果

if (errors.length) {
  for (const e of errors) console.error(e);
  console.error(`\n共 ${errors.length} 个问题。知识库规范见 .agents/CONTRIBUTING.md。`);
  process.exit(1);
}

console.log(`ok: ${notes.length} 篇笔记、${Object.keys(GENERATED).length} 份生成物、${DOC_FILES.length} 份文档检查通过`);
