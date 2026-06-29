"use client";

import { motion } from "framer-motion";
import Link from "next/link";
import { CharByChar } from "@/components/CharByChar";

const techStack = [
  { name: "WinUI 3", desc: "原生 UI 框架" },
  { name: ".NET 8", desc: "高性能运行时" },
  { name: "C#", desc: "开发语言" },
  { name: "AI 驱动", desc: "MiMo + Codex" },
];

export default function AboutPage() {
  return (
    <div className="pt-28 pb-16 px-4 sm:px-6 lg:px-8">
      <div className="max-w-4xl mx-auto">
        {/* Hero */}
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5 }} className="text-center mb-20">
          <h1 className="text-4xl sm:text-5xl font-bold mb-4"><CharByChar text="关于 DeskBox" /></h1>
          <p className="text-[var(--secondary)] text-lg max-w-2xl mx-auto">一个由产品经理发起、用 AI 构建的 Windows 桌面整理工具</p>
        </motion.div>

        {/* Origin Story - Full Width */}
        <motion.div
          initial={{ opacity: 0, y: 30 }}
          whileInView={{ opacity: 1, y: 0 }}
          viewport={{ once: true, margin: "-50px" }}
          transition={{ duration: 0.6 }}
          className="fluent-card mb-10"
        >
          <div className="flex items-center gap-3 mb-6">
            <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center text-[var(--accent)]">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"/></svg>
            </div>
            <h2 className="text-2xl font-bold">为什么做这个</h2>
          </div>
          <div className="text-[var(--secondary)] space-y-4 text-[15px] leading-relaxed">
            <p>Windows 发展了这么多年，桌面始终是用户最高频使用的界面。文件一多，桌面就变成垃圾场——这是每个 Windows 用户都经历过的痛点。</p>
            <p>微软从来没有认真解决过这个问题。市面上的整理工具要么太重，要么改变了原有的使用习惯。</p>
            <p>于是我自己动手做了一个。<strong className="text-[var(--foreground)]">DeskBox 不会替换你的桌面，只是在上面加一层更好用的整理能力。</strong></p>
          </div>
        </motion.div>

        {/* Two Column: Tech + Win10 */}
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-10">
          <motion.div
            initial={{ opacity: 0, y: 30 }}
            whileInView={{ opacity: 1, y: 0 }}
            viewport={{ once: true, margin: "-50px" }}
            transition={{ duration: 0.6, delay: 0.1 }}
            className="fluent-card"
          >
            <div className="flex items-center gap-3 mb-5">
              <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center text-[var(--accent)]">
                <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>
              </div>
              <h2 className="text-xl font-bold">技术选择</h2>
            </div>
            <div className="text-[var(--secondary)] text-sm space-y-3 leading-relaxed">
              <p>选择 <strong className="text-[var(--foreground)]">WinUI 3 + .NET 8</strong>，因为桌面工具就该像系统的一部分，而不是外挂。</p>
              <p>圆角、透明、动画这些现代 UI 效果，同时保持极低的资源占用——这是 Electron 或其他跨平台方案做不到的。</p>
            </div>
            <div className="grid grid-cols-2 gap-3 mt-5">
              {techStack.map((t) => (
                <div key={t.name} className="p-3 rounded-lg bg-[var(--background)] text-center">
                  <div className="font-semibold text-sm">{t.name}</div>
                  <div className="text-xs text-[var(--secondary)] mt-0.5">{t.desc}</div>
                </div>
              ))}
            </div>
          </motion.div>

          <motion.div
            initial={{ opacity: 0, y: 30 }}
            whileInView={{ opacity: 1, y: 0 }}
            viewport={{ once: true, margin: "-50px" }}
            transition={{ duration: 0.6, delay: 0.2 }}
            className="fluent-card"
          >
            <div className="flex items-center gap-3 mb-5">
              <div className="w-10 h-10 rounded-lg bg-amber-50 flex items-center justify-center text-amber-600">
                <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>
              </div>
              <h2 className="text-xl font-bold">关于 Win10</h2>
            </div>
            <div className="text-[var(--secondary)] text-sm space-y-3 leading-relaxed">
              <p>目前 DeskBox 在 Win10 上只能使用基础功能，外观定制暂时不可用。</p>
              <p>原因是 WinUI 3 的部分 API 在 Win10 上不支持，强行适配会牺牲稳定性和性能。</p>
              <p>后续会探索兼容方案，但<strong className="text-[var(--foreground)]">不会为了兼容而降低整体体验</strong>。</p>
            </div>
          </motion.div>
        </div>

        {/* Open Source + About Me - Merged */}
        <motion.div
          initial={{ opacity: 0, y: 30 }}
          whileInView={{ opacity: 1, y: 0 }}
          viewport={{ once: true, margin: "-50px" }}
          transition={{ duration: 0.6, delay: 0.1 }}
          className="fluent-card mb-10"
        >
          <div className="flex items-center gap-3 mb-6">
            <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center text-[var(--accent)]">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>
            </div>
            <h2 className="text-2xl font-bold">关于我</h2>
          </div>
          <div className="text-[var(--secondary)] text-[15px] space-y-4 leading-relaxed">
            <p>我叫<strong className="text-[var(--foreground)]">朱天雨</strong>，主业是产品经理。DeskBox 的起源很简单——工作中桌面文件越来越多，想找一个契合 Win 原生风格的整理工具，发现没有合适的，索性自己用 AI 开发了一个。</p>
            <p>坦白说，我并不懂代码。这个项目基本都是使用<strong className="text-[var(--foreground)]">小米 MiMo 2.5 Pro</strong> 开发的，部分使用了 <strong className="text-[var(--foreground)]">Codex GPT-5.5</strong>。AI 让我这样一个非程序员也能做出有用的东西，这本身就是一件很酷的事。</p>
            <p>DeskBox 一开始只是放在 GitHub 上的个人项目，没想到有不少人在用。国内用户访问 GitHub 不太方便，所以开了一个公众号<strong className="text-[var(--foreground)]">「大雨实验室」</strong>，有问题可以留言反馈。Bug 可能比较多，但下班后有时间都会去优化。</p>
          </div>
          <div className="mt-6 pt-6 border-t border-[var(--card-border)]">
            <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
              <div className="text-sm text-[var(--secondary)]">
                <span className="font-medium text-[var(--foreground)]">开源协议：</span>MIT · 完全免费 · 转载或二次开发请注明出处
              </div>
              <div className="flex gap-3">
                <a href="https://github.com/Tianyu199509/DeskBox" target="_blank" rel="noopener noreferrer" className="fluent-button text-sm py-2 px-5">GitHub</a>
                <Link href="/roadmap" className="fluent-button-secondary text-sm py-2 px-5">路线图</Link>
              </div>
            </div>
          </div>
        </motion.div>
      </div>
    </div>
  );
}
