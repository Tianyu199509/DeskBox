"use client";

import { motion } from "framer-motion";
import { CharByChar } from "@/components/CharByChar";

interface RoadmapItem {
  title: string;
  desc: string;
  tag?: string;
}

const phases: { phase: string; label: string; color: string; icon: React.ReactNode; items: RoadmapItem[] }[] = [
  {
    phase: "P1",
    label: "架构铺底",
    color: "var(--accent)",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/>
      </svg>
    ),
    items: [
      { title: "统一 WidgetShell", desc: "抽取通用外壳，新增格子只写内容", tag: "进行中" },
      { title: "注册表 / 工厂", desc: "格子类型集中管理" },
      { title: "随记迁移", desc: "验证 Shell 架构" },
      { title: "文件格子迁移", desc: "保持原有拖拽行为" },
      { title: "会话状态管理", desc: "F7 / 托盘 / 置顶统一管理" },
    ],
  },
  {
    phase: "P2",
    label: "功能格子",
    color: "#8B5CF6",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="3" width="18" height="18" rx="2" ry="2"/><line x1="3" y1="9" x2="21" y2="9"/><line x1="9" y1="21" x2="9" y2="9"/>
      </svg>
    ),
    items: [
      { title: "天气格子", desc: "定位 + 手动选城市" },
      { title: "系统监控", desc: "CPU / 内存 / 网络" },
      { title: "音乐控制", desc: "Windows 系统媒体会话" },
      { title: "独立 Todo", desc: "不并入随记" },
      { title: "标签系统", desc: "内部索引，不写入文件" },
    ],
  },
  {
    phase: "P3",
    label: "体验增强",
    color: "#F59E0B",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/>
      </svg>
    ),
    items: [
      { title: "格子合并", desc: "同类文件格子合并" },
      { title: "多显示器", desc: "跨屏自由移动" },
      { title: "在线更新", desc: "自动检测并更新" },
      { title: "云同步", desc: "设置和配置跨设备" },
    ],
  },
];

export default function RoadmapPage() {
  return (
    <div className="pt-28 pb-20 px-4 sm:px-6 lg:px-8">
      <div className="max-w-5xl mx-auto">
        {/* Header */}
        <motion.div
          initial={{ opacity: 0, y: 20 }}
          animate={{ opacity: 1, y: 0 }}
          className="text-center mb-20"
        >
          <h1 className="text-4xl sm:text-5xl font-bold mb-5"><CharByChar text="未来规划" /></h1>
          <p className="text-[var(--secondary)] text-lg max-w-xl mx-auto">DeskBox 的发展路线图</p>
        </motion.div>

        {/* Phase Cards */}
        <div className="space-y-12">
          {phases.map((phase, pi) => (
            <motion.div
              key={phase.phase}
              initial={{ opacity: 0, y: 30 }}
              whileInView={{ opacity: 1, y: 0 }}
              viewport={{ once: true, margin: "-40px" }}
              transition={{ duration: 0.6, delay: pi * 0.1 }}
            >
              {/* Phase Header */}
              <div className="flex items-center gap-4 mb-7">
                <div
                  className="w-11 h-11 rounded-xl flex items-center justify-center text-white shadow-md"
                  style={{ background: phase.color }}
                >
                  {phase.icon}
                </div>
                <div>
                  <h2 className="text-xl font-bold">{phase.label}</h2>
                  <p className="text-[var(--secondary)] text-sm">{phase.items.length} 项规划</p>
                </div>
                <div className="flex-1 h-0.5 bg-gradient-to-r from-[var(--card-border)] to-transparent ml-4" />
              </div>

              {/* Items Grid */}
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                {phase.items.map((item, ii) => (
                  <div
                    key={ii}
                    className="fluent-card !p-5 flex items-start gap-3.5"
                  >
                    <div
                      className="w-2 h-2 rounded-full mt-2 flex-shrink-0"
                      style={{ background: phase.color }}
                    />
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2 mb-1">
                        <h3 className="font-semibold text-[15px]">{item.title}</h3>
                        {item.tag && (
                          <span
                            className="text-[11px] px-2 py-0.5 rounded-full font-medium"
                            style={{
                              color: phase.color,
                              background: `color-mix(in srgb, ${phase.color} 10%, transparent)`,
                            }}
                          >
                            {item.tag}
                          </span>
                        )}
                      </div>
                      <p className="text-[var(--secondary)] text-sm leading-relaxed">{item.desc}</p>
                    </div>
                  </div>
                ))}
              </div>
            </motion.div>
          ))}
        </div>

        {/* CTA */}
        <motion.div
          initial={{ opacity: 0, y: 20 }}
          whileInView={{ opacity: 1, y: 0 }}
          viewport={{ once: true }}
          className="mt-20 text-center"
        >
          <div className="fluent-card inline-flex flex-col items-center gap-5 py-10 px-12">
            <p className="text-[var(--secondary)] text-lg">有想法？欢迎在 GitHub 提出建议</p>
            <a
              href="https://github.com/Tianyu199509/DeskBox/issues"
              target="_blank"
              rel="noopener noreferrer"
              className="fluent-button"
            >
              在 GitHub 提交建议
            </a>
          </div>
        </motion.div>
      </div>
    </div>
  );
}
