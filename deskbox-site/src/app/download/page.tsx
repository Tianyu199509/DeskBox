"use client";

import { useState } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { CharByChar } from "@/components/CharByChar";

const versions = [
  { version: "1.1.10", date: "2026-06-29", size: "21.7 MB" },
  { version: "1.1.9", date: "2026-06-29", size: "21.7 MB" },
  { version: "1.1.8", date: "2026-06-29", size: "21.7 MB" },
  { version: "1.1.6", date: "2026-06-29", size: "21.7 MB" },
  { version: "1.1.5", date: "2026-06-29", size: "21.7 MB" },
  { version: "1.1.4", date: "2026-06-28", size: "21.7 MB" },
  { version: "1.1.3", date: "2026-06-27", size: "21.7 MB" },
  { version: "1.1.2", date: "2026-06-26", size: "21.7 MB" },
  { version: "1.1.1", date: "2026-06-26", size: "21.7 MB" },
  { version: "1.1.0", date: "2026-06-26", size: "21.7 MB" },
  { version: "1.0.9", date: "2026-06-25", size: "21.7 MB" },
  { version: "1.0.8", date: "2026-06-24", size: "21.7 MB" },
];

export default function DownloadPage() {
  const [showAll, setShowAll] = useState(false);
  const visibleVersions = showAll ? versions : versions.slice(0, 5);

  return (
    <div className="pt-28 pb-16 px-4 sm:px-6 lg:px-8">
      <div className="max-w-4xl mx-auto">
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5 }} className="text-center mb-16">
          <h1 className="text-4xl font-bold mb-4"><CharByChar text="下载 DeskBox" /></h1>
          <p className="text-[var(--secondary)]">免费开源 · Windows 11/10</p>
        </motion.div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.1 }} className="fluent-card text-center mb-12 relative overflow-hidden">
          <div className="absolute top-0 left-0 right-0 h-1 bg-gradient-to-r from-[var(--accent)] to-[var(--accent-hover)]" />
          <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full bg-[var(--accent-light)] text-[var(--accent)] text-sm font-medium mb-4">最新版本</div>
          <h2 className="text-3xl font-bold mb-2">DeskBox v1.1.10</h2>
          <p className="text-[var(--secondary)] mb-6">发布日期：2026-06-29 · 21.7 MB</p>
          <a href="https://github.com/Tianyu199509/DeskBox/releases/download/v1.1.10/DeskBox_Setup_1.1.10_x64.exe" className="fluent-button fluent-button-primary-shimmer text-lg px-8 py-3 inline-block">下载 x64 安装包</a>
        </motion.div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.2 }} className="fluent-card mb-12">
          <h3 className="text-xl font-semibold mb-4">系统要求</h3>
          <ul className="space-y-3 text-[var(--secondary)]">
            <li className="flex items-center gap-2"><svg className="w-4 h-4 text-emerald-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor"><path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" /></svg>Windows 11（推荐）或 Windows 10</li>
            <li className="flex items-center gap-2"><svg className="w-4 h-4 text-emerald-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor"><path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" /></svg>.NET 8 Runtime x64（安装器自动下载）</li>
            <li className="flex items-center gap-2"><svg className="w-4 h-4 text-emerald-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor"><path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" /></svg>Windows App Runtime 2.1.3（安装器自动下载）</li>
          </ul>
        </motion.div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.3 }}>
          <h3 className="text-xl font-semibold mb-6">历史版本</h3>
          <div className="space-y-3">
            {visibleVersions.map((v) => (
              <div key={v.version} className="fluent-card flex items-center justify-between group">
                <div>
                  <span className="font-medium">v{v.version}</span>
                  <span className="text-[var(--secondary)] text-sm ml-3">{v.date}</span>
                </div>
                <div className="flex items-center gap-4">
                  <span className="text-[var(--secondary)] text-sm">{v.size}</span>
                  <a href={`https://github.com/Tianyu199509/DeskBox/releases/download/v${v.version}/DeskBox_Setup_${v.version}_x64.exe`} className="text-[var(--accent)] hover:underline text-sm opacity-0 group-hover:opacity-100 transition-opacity">下载</a>
                  <a href={`/changelog#v${v.version.replace(/\./g, "")}`} className="text-[var(--secondary)] hover:text-[var(--foreground)] text-sm opacity-0 group-hover:opacity-100 transition-opacity">更新日志</a>
                </div>
              </div>
            ))}
          </div>
          {versions.length > 5 && (
            <button
              onClick={() => setShowAll(!showAll)}
              className="mt-4 text-[var(--accent)] text-sm font-medium hover:underline"
            >
              {showAll ? "收起" : `显示全部 ${versions.length} 个版本`}
            </button>
          )}
        </motion.div>
      </div>
    </div>
  );
}
