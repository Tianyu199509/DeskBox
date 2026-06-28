"use client";

import { motion } from "framer-motion";

const roadmapItems = [
  { title: "在线更新", description: "客户端自动检测新版本并一键更新" },
  { title: "插件系统", description: "支持第三方插件扩展格子功能" },
  { title: "更多格子类型", description: "待办清单、天气、时钟等实用格子" },
  { title: "多显示器支持", description: "格子可以在多个显示器间自由移动" },
  { title: "云同步", description: "设置和格子配置跨设备同步" },
];

export default function RoadmapPage() {
  return (
    <div className="py-16 px-4 sm:px-6 lg:px-8">
      <div className="max-w-4xl mx-auto">
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5 }} className="text-center mb-16">
          <h1 className="text-4xl font-bold mb-4">未来规划</h1>
          <p className="text-[var(--secondary)] max-w-2xl mx-auto">DeskBox 的发展路线图</p>
        </motion.div>
        <div className="space-y-6">
          {roadmapItems.map((item, index) => (
            <motion.div key={index} initial={{ opacity: 0, x: -20 }} animate={{ opacity: 1, x: 0 }} transition={{ duration: 0.5, delay: index * 0.1 }} className="fluent-card flex items-start gap-4">
              <div className="w-8 h-8 rounded-full bg-[var(--accent-light)] flex items-center justify-center flex-shrink-0">
                <div className="w-3 h-3 rounded-full bg-[var(--accent)]" />
              </div>
              <div>
                <h3 className="font-semibold text-lg">{item.title}</h3>
                <p className="text-[var(--secondary)] mt-1">{item.description}</p>
              </div>
            </motion.div>
          ))}
        </div>
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.5, delay: 0.5 }} className="mt-12 text-center">
          <p className="text-[var(--secondary)] mb-4">有功能建议？欢迎在 GitHub 提出</p>
          <a href="https://github.com/Tianyu199509/DeskBox/issues" target="_blank" rel="noopener noreferrer" className="fluent-button">提交建议</a>
        </motion.div>
      </div>
    </div>
  );
}
