"use client";

import { useState, useEffect } from "react";
import { motion } from "framer-motion";
import { CharByChar } from "@/components/CharByChar";

interface ChangelogEntry { version: string; date: string; english: string[]; chinese: string[]; }

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

export default function ChangelogPage() {
  const [entries, setEntries] = useState<ChangelogEntry[]>([]);
  const [lang, setLang] = useState<"en" | "zh">("zh");

  useEffect(() => {
    fetch("https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/CHANGELOG.md")
      .then((res) => res.text())
      .then((text) => setEntries(parseChangelog(text)))
      .catch(() => {});
  }, []);

  return (
    <div className="pt-28 pb-16 px-4 sm:px-6 lg:px-8">
      <div className="max-w-4xl mx-auto">
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5 }} className="flex justify-between items-center mb-12">
          <div>
            <h1 className="text-4xl font-bold mb-2"><CharByChar text="更新日志" /></h1>
            <p className="text-[var(--secondary)]">DeskBox 版本更新记录</p>
          </div>
          <div className="flex gap-2">
            <button onClick={() => setLang("zh")} className={`px-4 py-2 rounded-lg text-sm transition-colors ${lang === "zh" ? "bg-[var(--accent)] text-white" : "bg-[var(--card-border)] text-[var(--secondary)]"}`}>中文</button>
            <button onClick={() => setLang("en")} className={`px-4 py-2 rounded-lg text-sm transition-colors ${lang === "en" ? "bg-[var(--accent)] text-white" : "bg-[var(--card-border)] text-[var(--secondary)]"}`}>English</button>
          </div>
        </motion.div>
        <div className="space-y-8">
          {entries.map((entry, index) => (
            <motion.div key={entry.version} id={`v${entry.version.replace(/\./g, "")}`} initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5, delay: index * 0.05 }} className="fluent-card">
              <div className="flex items-center gap-3 mb-4">
                <h2 className="text-2xl font-bold">v{entry.version}</h2>
                <span className="text-[var(--secondary)] text-sm">{entry.date}</span>
              </div>
              <ul className="space-y-2">
                {(lang === "en" ? entry.english : entry.chinese).map((item, i) => (
                  <li key={i} className="flex items-start gap-2">
                    <span className="text-[var(--accent)] mt-1">•</span>
                    <span className="text-[var(--secondary)]">{item}</span>
                  </li>
                ))}
              </ul>
            </motion.div>
          ))}
        </div>
        {entries.length === 0 && <div className="text-center py-20 text-[var(--secondary)]">加载中...</div>}
      </div>
    </div>
  );
}
