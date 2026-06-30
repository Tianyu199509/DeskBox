"use client";

import { useState, useEffect, useMemo } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { CharByChar } from "@/components/CharByChar";

interface ChangelogEntry {
  version: string;
  date: string;
  english: string[];
  chinese: string[];
}

function parseChangelog(content: string): ChangelogEntry[] {
  const entries: ChangelogEntry[] = [];
  const versionBlocks = content.split(/^## /m).filter(Boolean);
  for (const block of versionBlocks) {
    const lines = block.split("\n");
    const headerMatch = lines[0].match(/^(\d+\.\d+\.\d+)\s*-\s*(\d{4}-\d{2}-\d{2})/);
    if (!headerMatch) continue;
    const version = headerMatch[1];
    const date = headerMatch[2];
    const english: string[] = [];
    const chinese: string[] = [];
    let currentLang = "";
    for (let i = 1; i < lines.length; i++) {
      const line = lines[i].trim();
      if (line === "### English") { currentLang = "en"; continue; }
      if (line === "### 中文") { currentLang = "zh"; continue; }
      if (line.startsWith("- ")) {
        if (currentLang === "en") english.push(line.substring(2));
        if (currentLang === "zh") chinese.push(line.substring(2));
      }
    }
    entries.push({ version, date, english, chinese });
  }
  return entries;
}

function classifyItem(item: string): { type: string; color: string; bg: string } {
  const lower = item.toLowerCase();
  if (/新增|add|new|支持|implement/i.test(lower)) return { type: "新增", color: "#16a34a", bg: "#f0fdf4" };
  if (/修复|fix|bug|crash|问题/i.test(lower)) return { type: "修复", color: "#ea580c", bg: "#fff7ed" };
  if (/优化|improve|update|增强|调整|重构|refactor/i.test(lower)) return { type: "优化", color: "#2563eb", bg: "#eff6ff" };
  return { type: "变更", color: "#6b7280", bg: "#f9fafb" };
}

function SkeletonLoader() {
  return (
    <div className="space-y-6">
      {[1, 2, 3].map((i) => (
        <div key={i} className="fluent-card animate-pulse">
          <div className="flex items-center gap-3 mb-4">
            <div className="h-7 w-24 bg-[var(--card-border)] rounded-lg" />
            <div className="h-4 w-20 bg-[var(--card-border)] rounded" />
          </div>
          <div className="space-y-3">
            {[1, 2, 3].map((j) => (
              <div key={j} className="flex items-center gap-2">
                <div className="h-5 w-12 bg-[var(--card-border)] rounded" />
                <div className="h-4 flex-1 bg-[var(--card-border)] rounded" />
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

export default function ChangelogPage() {
  const [entries, setEntries] = useState<ChangelogEntry[]>([]);
  const [lang, setLang] = useState<"zh" | "en">("zh");
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const cacheKey = "deskbox_changelog";
    const cacheTTL = 6 * 60 * 60 * 1000;

    try {
      const cached = localStorage.getItem(cacheKey);
      if (cached) {
        const { data, ts } = JSON.parse(cached);
        if (Date.now() - ts < cacheTTL) {
          setEntries(data);
          setLoading(false);
          setExpanded(new Set(data.length > 0 ? [data[0].version] : []));
          return;
        }
      }
    } catch {}

    fetch("https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/CHANGELOG.md")
      .then((res) => res.text())
      .then((text) => {
        const data = parseChangelog(text);
        setEntries(data);
        setLoading(false);
        setExpanded(new Set(data.length > 0 ? [data[0].version] : []));
        try {
          localStorage.setItem(cacheKey, JSON.stringify({ data, ts: Date.now() }));
        } catch {}
      })
      .catch(() => setLoading(false));
  }, []);

  const toggleExpand = (version: string) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(version)) next.delete(version);
      else next.add(version);
      return next;
    });
  };

  const allExpanded = entries.length > 0 && expanded.size === entries.length;

  const toggleAll = () => {
    if (allExpanded) {
      setExpanded(new Set());
    } else {
      setExpanded(new Set(entries.map((e) => e.version)));
    }
  };

  return (
    <div className="pt-28 pb-16 px-4 sm:px-6 lg:px-8">
      <div className="max-w-4xl mx-auto">
        {/* Header */}
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} className="flex flex-col sm:flex-row sm:items-end justify-between gap-4 mb-12">
          <div>
            <h1 className="text-4xl sm:text-5xl font-bold mb-3"><CharByChar text="更新日志" /></h1>
            <p className="text-[var(--secondary)] text-lg">DeskBox 版本更新记录</p>
          </div>
          {/* Controls */}
          <div className="flex items-center gap-2 self-start">
            <div className="flex items-center bg-[var(--card-background)] border border-[var(--card-border)] rounded-lg p-1">
              <button
                onClick={() => setLang("zh")}
                className={`relative px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 ${
                  lang === "zh" ? "text-[var(--foreground)] shadow-sm" : "text-[var(--secondary)]"
                }`}
                style={lang === "zh" ? { background: "var(--background)" } : {}}
              >
                中文
              </button>
              <button
                onClick={() => setLang("en")}
                className={`relative px-4 py-1.5 rounded-md text-sm font-medium transition-all duration-200 ${
                  lang === "en" ? "text-[var(--foreground)] shadow-sm" : "text-[var(--secondary)]"
                }`}
                style={lang === "en" ? { background: "var(--background)" } : {}}
              >
                English
              </button>
            </div>
            {!loading && entries.length > 0 && (
              <div className="flex items-center bg-[var(--card-background)] border border-[var(--card-border)] rounded-lg p-1">
                <button
                  onClick={toggleAll}
                  className="px-4 py-1.5 rounded-md text-sm font-medium text-[var(--secondary)] hover:text-[var(--foreground)] transition-all"
                  style={{ background: "var(--background)" }}
                >
                  {allExpanded ? "收起全部" : "展开全部"}
                </button>
              </div>
            )}
          </div>
        </motion.div>

        {/* Content */}
        {loading ? (
          <SkeletonLoader />
        ) : (
          <div className="space-y-4">
            {entries.map((entry, index) => {
              const isOpen = expanded.has(entry.version);
              const items = lang === "en" ? entry.english : entry.chinese;

              return (
                <motion.div
                  key={entry.version}
                  id={`v${entry.version.replace(/\./g, "")}`}
                  initial={{ opacity: 0, y: 16 }}
                  whileInView={{ opacity: 1, y: 0 }}
                  viewport={{ once: true, margin: "-20px" }}
                  transition={{ duration: 0.4, delay: index < 3 ? index * 0.05 : 0 }}
                  className="fluent-card !p-0 overflow-hidden"
                >
                  {/* Version Header - Clickable */}
                  <button
                    onClick={() => toggleExpand(entry.version)}
                    className="w-full flex items-center justify-between p-5 text-left cursor-pointer hover:bg-[var(--background)]/50 transition-colors"
                    style={{ background: "transparent", border: "none" }}
                  >
                    <div className="flex items-center gap-3">
                      <span className="text-xl font-bold">v{entry.version}</span>
                      {index === 0 && (
                        <span className="text-[11px] px-2 py-0.5 rounded-full font-medium bg-[var(--accent-light)] text-[var(--accent)]">
                          最新
                        </span>
                      )}
                      <span className="text-[var(--secondary)] text-sm">{entry.date}</span>
                    </div>
                    <motion.svg
                      animate={{ rotate: isOpen ? 180 : 0 }}
                      transition={{ duration: 0.2 }}
                      className="w-5 h-5 text-[var(--secondary)]"
                      viewBox="0 0 20 20"
                      fill="currentColor"
                    >
                      <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
                    </motion.svg>
                  </button>

                  {/* Items */}
                  <AnimatePresence initial={false}>
                    {isOpen && (
                      <motion.div
                        initial={{ height: 0, opacity: 0 }}
                        animate={{ height: "auto", opacity: 1 }}
                        exit={{ height: 0, opacity: 0 }}
                        transition={{ duration: 0.3, ease: [0.16, 1, 0.3, 1] }}
                        style={{ overflow: "hidden" }}
                      >
                        <div className="px-5 pb-5 pt-0 border-t border-[var(--card-border)]">
                          <div className="pt-4 space-y-2.5">
                            {items.map((item, i) => {
                              const cls = classifyItem(item);
                              return (
                                <div key={i} className="flex items-start gap-2.5">
                                  <span
                                    className="text-[11px] px-2 py-0.5 rounded font-medium flex-shrink-0 mt-0.5"
                                    style={{ color: cls.color, backgroundColor: cls.bg }}
                                  >
                                    {cls.type}
                                  </span>
                                  <span className="text-[var(--secondary)] text-sm leading-relaxed">{item}</span>
                                </div>
                              );
                            })}
                          </div>
                        </div>
                      </motion.div>
                    )}
                  </AnimatePresence>
                </motion.div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
