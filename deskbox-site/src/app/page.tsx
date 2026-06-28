"use client";

import { motion } from "framer-motion";
import Image from "next/image";
import Link from "next/link";

const FolderIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M2 6C2 4.89543 2.89543 4 4 4H9L11 6H20C21.1046 6 22 6.89543 22 8V18C22 19.1046 21.1046 20 20 20H4C2.89543 20 2 19.1046 2 18V6Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const BookIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M21 5C21 3.9 20.1 3 19 3H7C5.9 3 5 3.9 5 5V21L12 18L19 21V5ZM19 5L12 7.25L5 5V18.5L12 15.75L19 18.5V5Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const MapIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M9 2L7.17 4H4C2.9 4 2 4.9 2 6V18C2 19.1 2.9 20 4 20H20C21.1 20 22 19.1 22 18V6C22 4.9 21.1 4 20 4H16.83L15 2H9ZM12 17C9.24 17 7 14.76 7 12C7 9.24 9.24 7 12 7C14.76 7 17 9.24 17 12C17 14.76 14.76 17 12 17Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const scenarios = [
  { Icon: FolderIcon, title: "办公桌整理", desc: "把散落在桌面的文档、表格、快捷方式收进不同格子，需要时一键唤起。" },
  { Icon: BookIcon, title: "学习资料管理", desc: "课程资料、笔记、参考文档按科目分格，不再翻文件夹找文件。" },
  { Icon: MapIcon, title: "项目文件归档", desc: "把已有项目文件夹映射为桌面格子，不移动文件，原地管理。" },
];

export default function Home() {
  return (
    <div className="min-h-screen">
      {/* Hero */}
      <section className="pt-32 pb-20 px-4 sm:px-6 lg:px-8">
        <div className="max-w-4xl mx-auto text-center">
          <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: [0.16, 1, 0.3, 1] }}>
            <div className="flex items-center justify-center gap-3 mb-8">
              <Image src="/deskbox-logo-static.svg" alt="DeskBox" width={48} height={48} />
              <span className="text-3xl font-semibold tracking-tight">DeskBox</span>
            </div>
            <h1 className="text-5xl sm:text-6xl lg:text-7xl font-bold mb-6 leading-[1.05]">
              把桌面文件<br /><span className="text-[var(--accent)]">收进格子里</span>
            </h1>
            <p className="text-xl text-[var(--secondary)] mb-10 max-w-xl mx-auto leading-relaxed">
              Windows 11 桌面整理工具。创建格子收纳文件、映射文件夹、管理剪贴板。
            </p>
            <div className="flex flex-wrap justify-center gap-4">
              <Link href="/download" className="fluent-button text-lg px-10 py-4">免费下载</Link>
              <Link href="/features" className="fluent-button-secondary text-lg px-10 py-4">了解功能</Link>
            </div>
            <p className="text-sm text-[var(--secondary)] mt-6">v1.1.4 · Windows 11/10 · 免费开源</p>
          </motion.div>
        </div>
      </section>

      {/* Product Screenshots */}
      <section className="px-4 sm:px-6 lg:px-8 pb-24">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 40 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true, margin: "-50px" }} transition={{ duration: 0.8, ease: [0.16, 1, 0.3, 1] }} className="relative">
            <div className="absolute inset-0 bg-gradient-to-b from-[var(--accent)]/10 to-transparent rounded-3xl blur-3xl" />
            <Image src="/screenshots/product-cover-1280x720.png" alt="DeskBox 界面截图" width={1280} height={720} className="relative rounded-2xl border border-[var(--card-border)] shadow-2xl" priority />
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mt-4">
            <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: 0.1 }}>
              <Image src="/screenshots/widget-light.png" alt="DeskBox 浅色模式" width={640} height={400} className="rounded-xl border border-[var(--card-border)] shadow-lg" />
            </motion.div>
            <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: 0.2 }}>
              <Image src="/screenshots/widget-dark.png" alt="DeskBox 深色模式" width={640} height={400} className="rounded-xl border border-[var(--card-border)] shadow-lg" />
            </motion.div>
          </div>
        </div>
      </section>

      {/* Scenarios */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5 }} className="mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3">用在这些场景</h2>
            <p className="text-[var(--secondary)] text-lg">不是替代桌面，是帮桌面多一层整理能力</p>
          </motion.div>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {scenarios.map((s, i) => (
              <motion.div key={i} initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5, delay: i * 0.1 }} className="fluent-card">
                <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center mb-4 text-[var(--accent)]">
                  <s.Icon />
                </div>
                <h3 className="text-lg font-semibold mb-2">{s.title}</h3>
                <p className="text-[var(--secondary)] text-sm leading-relaxed">{s.desc}</p>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* Features Summary */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3">核心功能</h2>
            <p className="text-[var(--secondary)] text-lg">简洁高效，专注于桌面文件整理</p>
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-6">
            {[
              { title: "收纳格子", desc: "创建桌面格子，拖拽文件入格，支持排序、搜索、批量操作" },
              { title: "文件夹映射", desc: "将已有文件夹映射为格子，不移动文件，原地管理" },
              { title: "随记", desc: "自动记录剪贴板内容，支持文本、链接、截图，随时调用" },
              { title: "全局快捷键", desc: "F7 一键唤起，全屏应用下也能使用，拖拽内容到其他应用" },
              { title: "外观定制", desc: "明暗主题、透明度、圆角、动画效果，全部可调" },
              { title: "拖拽诊断", desc: "一键修复 Windows 10/11 拖拽兼容性问题" },
            ].map((f, i) => (
              <motion.div key={i} initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: i * 0.05 }} className="fluent-card">
                <h3 className="font-semibold mb-2">{f.title}</h3>
                <p className="text-[var(--secondary)] text-sm">{f.desc}</p>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* CTA */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-3xl mx-auto text-center">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5 }}>
            <h2 className="text-3xl font-bold mb-3">免费下载</h2>
            <p className="text-[var(--secondary)] mb-8">21 MB · Windows 11/10 · 运行时依赖自动安装</p>
            <Link href="/download" className="fluent-button text-lg px-10 py-4">下载 DeskBox v1.1.4</Link>
          </motion.div>
        </div>
      </section>
    </div>
  );
}
