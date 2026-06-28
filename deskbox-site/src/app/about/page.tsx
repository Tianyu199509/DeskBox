"use client";

import { motion } from "framer-motion";
import Link from "next/link";

export default function AboutPage() {
  return (
    <div className="py-16 px-4 sm:px-6 lg:px-8">
      <div className="max-w-4xl mx-auto">
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5 }} className="text-center mb-16">
          <h1 className="text-4xl font-bold mb-4">关于 DeskBox</h1>
          <p className="text-[var(--secondary)] max-w-2xl mx-auto">一个为 Windows 11 打造的轻量桌面整理工具</p>
        </motion.div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.1 }} className="fluent-card mb-8">
          <h2 className="text-2xl font-bold mb-4">开发故事</h2>
          <div className="text-[var(--secondary)] space-y-4">
            <p>DeskBox 诞生于一个简单的痛点：Windows 桌面上的文件越来越多，但现有的整理工具要么太重，要么改变了太多原有的使用习惯。</p>
            <p>我想要的是一个轻量的、原生的、不打扰的桌面整理方案。于是 DeskBox 诞生了——它不会替换你的桌面，只是在上面加一层更好用的整理能力。</p>
          </div>
        </motion.div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.2 }} className="fluent-card mb-8">
          <h2 className="text-2xl font-bold mb-4">技术栈</h2>
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
            {[{ name: "WinUI 3", desc: "UI 框架" }, { name: ".NET 8", desc: "运行时" }, { name: "Windows App SDK", desc: "系统集成" }, { name: "C#", desc: "开发语言" }].map((tech) => (
              <div key={tech.name} className="text-center p-4 rounded-lg bg-[var(--background)]">
                <div className="font-semibold">{tech.name}</div>
                <div className="text-sm text-[var(--secondary)]">{tech.desc}</div>
              </div>
            ))}
          </div>
        </motion.div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.3 }} className="fluent-card">
          <h2 className="text-2xl font-bold mb-4">开源项目</h2>
          <div className="text-[var(--secondary)] space-y-4">
            <p>DeskBox 使用 MIT 许可证开源，欢迎贡献代码、报告问题或提出建议。</p>
            <div className="flex gap-4">
              <a href="https://github.com/Tianyu199509/DeskBox" target="_blank" rel="noopener noreferrer" className="fluent-button">GitHub 仓库</a>
              <Link href="/roadmap" className="fluent-button-secondary">查看路线图</Link>
            </div>
          </div>
        </motion.div>
      </div>
    </div>
  );
}
