"use client";

import { motion, useScroll, useTransform } from "framer-motion";
import Image from "next/image";
import Link from "next/link";
import { useState, useRef, useEffect } from "react";

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

function useGitHubStats() {
  const [stars, setStars] = useState(850);
  const [downloads, setDownloads] = useState(12000);

  useEffect(() => {
    const cacheKey = "deskbox_gh_stats";
    const cacheTTL = 24 * 60 * 60 * 1000;

    try {
      const cached = localStorage.getItem(cacheKey);
      if (cached) {
        const { stars: s, downloads: d, ts } = JSON.parse(cached);
        if (Date.now() - ts < cacheTTL) {
          setStars(s);
          setDownloads(d);
          return;
        }
      }
    } catch {}

    Promise.all([
      fetch("https://api.github.com/repos/Tianyu199509/DeskBox")
        .then((r) => r.json())
        .then((d) => d.stargazers_count || 850)
        .catch(() => 850),
      fetch("https://api.github.com/repos/Tianyu199509/DeskBox/releases")
        .then((r) => r.json())
        .then((releases: Array<{ assets: Array<{ download_count: number }> }>) =>
          releases.reduce((sum, r) => sum + r.assets.reduce((s, a) => s + a.download_count, 0), 0)
        )
        .catch(() => 12000),
    ]).then(([s, d]) => {
      setStars(s);
      setDownloads(d);
      try {
        localStorage.setItem(cacheKey, JSON.stringify({ stars: s, downloads: d, ts: Date.now() }));
      } catch {}
    });
  }, []);

  return { stars, downloads };
}

function CharByChar({ text, className, delay = 0 }: { text: string; className?: string; delay?: number }) {
  return (
    <span className={className} aria-label={text}>
      {text.split("").map((char, i) => (
        <motion.span
          key={i}
          initial={{ opacity: 0, y: 16, filter: "blur(4px)" }}
          whileInView={{ opacity: 1, y: 0, filter: "blur(0px)" }}
          viewport={{ once: true, margin: "-40px" }}
          transition={{ duration: 0.45, delay: delay + i * 0.035, ease: [0.16, 1, 0.3, 1] }}
          style={{ display: "inline-block", whiteSpace: char === " " ? "pre" : undefined }}
        >
          {char}
        </motion.span>
      ))}
    </span>
  );
}

function ParallaxImage({ src, alt, width, height, className }: { src: string; alt: string; width: number; height: number; className?: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const { scrollYProgress } = useScroll({ target: ref, offset: ["start end", "end start"] });
  const y = useTransform(scrollYProgress, [0, 1], [30, -30]);

  return (
    <div ref={ref} className={className}>
      <motion.div style={{ y }}>
        <Image src={src} alt={alt} width={width} height={height} className="w-full h-auto" />
      </motion.div>
    </div>
  );
}

function CountUp({ target, suffix = "" }: { target: number; suffix?: string }) {
  const [count, setCount] = useState(0);
  const [started, setStarted] = useState(false);

  return (
    <motion.span
      onViewportEnter={() => {
        if (started) return;
        setStarted(true);
        const duration = 1200;
        const steps = 40;
        const increment = target / steps;
        let current = 0;
        const timer = setInterval(() => {
          current += increment;
          if (current >= target) {
            setCount(target);
            clearInterval(timer);
          } else {
            setCount(Math.floor(current));
          }
        }, duration / steps);
      }}
      viewport={{ once: true, margin: "-50px" }}
    >
      {count.toLocaleString()}{suffix}
    </motion.span>
  );
}

function FAQItem({ question, answer }: { question: string; answer: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="fluent-card" style={{ padding: 0, overflow: "hidden" }}>
      <button
        onClick={() => setOpen(!open)}
        className="w-full flex items-center justify-between p-5 text-left cursor-pointer"
        style={{ background: "transparent", border: "none" }}
      >
        <span className="font-medium text-[var(--foreground)] pr-4">{question}</span>
        <motion.svg
          animate={{ rotate: open ? 180 : 0 }}
          transition={{ duration: 0.2 }}
          className="w-5 h-5 flex-shrink-0 text-[var(--secondary)]"
          viewBox="0 0 20 20" fill="currentColor"
        >
          <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
        </motion.svg>
      </button>
      <motion.div
        initial={false}
        animate={{ height: open ? "auto" : 0, opacity: open ? 1 : 0 }}
        transition={{ duration: 0.25, ease: [0.16, 1, 0.3, 1] }}
        style={{ overflow: "hidden" }}
      >
        <p className="px-5 pb-5 text-[var(--secondary)] text-sm leading-relaxed">{answer}</p>
      </motion.div>
    </div>
  );
}

const scenarios = [
  { Icon: FolderIcon, title: "办公桌整理", desc: "把散落在桌面的文档、表格、快捷方式收进不同格子，需要时一键唤起。" },
  { Icon: BookIcon, title: "学习资料管理", desc: "课程资料、笔记、参考文档按科目分格，不再翻文件夹找文件。" },
  { Icon: MapIcon, title: "项目文件归档", desc: "把已有项目文件夹映射为桌面格子，不移动文件，原地管理。" },
];

const workflowSteps = [
  { step: "01", title: "安装启动", desc: "下载安装包，双击运行。无需复杂配置，开箱即用。" },
  { step: "02", title: "创建格子", desc: "右键桌面或按 F7，一键创建收纳格子。支持自定义大小和位置。" },
  { step: "03", title: "拖入文件", desc: "把桌面文件直接拖进格子，按类型自动分类，也可以手动排序。" },
  { step: "04", title: "随时唤起", desc: "F7 全局快捷键一键呼出，全屏应用下也能使用。拖拽内容到其他应用。" },
];

const techHighlights = [
  { icon: "⚡", title: "WinUI 3 原生", desc: "基于最新 WinUI 3 框架，完美适配 Windows 11 Fluent Design，原生性能体验。" },
  { icon: "🚀", title: ".NET 8 驱动", desc: "使用 .NET 8 构建，启动速度快，内存占用低，后台常驻不卡顿。" },
  { icon: "🔓", title: "完全开源", desc: "MIT 协议开源，代码透明可审计。欢迎社区贡献，共同改进。" },
  { icon: "🎨", title: "Fluent Design", desc: "遵循微软 Fluent Design 设计语言，圆角、透明、动效，与系统浑然一体。" },
  { icon: "📦", title: "轻量无依赖", desc: "安装包仅 21 MB，运行时自动安装，不捆绑任何第三方软件。" },
  { icon: "🔧", title: "自动修复", desc: "内置拖拽诊断工具，一键修复 Windows 10/11 常见的拖拽兼容性问题。" },
];

const faqs = [
  { question: "DeskBox 是免费的吗？", answer: "完全免费，MIT 开源协议。你可以自由使用、修改和分发。" },
  { question: "支持 Windows 10 吗？", answer: "支持。推荐 Windows 11 以获得最佳体验，但 Windows 10 (1809+) 也能正常运行。" },
  { question: "安装后需要联网吗？", answer: "不需要。DeskBox 是纯本地应用，所有数据存储在你的电脑上，不会上传任何信息。" },
  { question: "和腾讯桌面整理有什么区别？", answer: "DeskBox 完全开源免费，无广告无捆绑。使用原生 WinUI 3 框架，性能更好，界面更现代。" },
  { question: "文件放在格子里会被移动吗？", answer: "收纳格子会移动文件，但文件夹映射功能不会。你可以选择适合自己的方式。" },
  { question: "支持多显示器吗？", answer: "目前暂不支持，多显示器是后续版本的优先开发计划。" },
];

export default function Home() {
  const { stars, downloads } = useGitHubStats();
  const stats = [
    { value: downloads, suffix: "+", label: "累计下载" },
    { value: stars, suffix: "+", label: "GitHub Stars" },
    { value: 40, suffix: "+", label: "功能特性" },
    { value: 21, suffix: " MB", label: "安装包大小" },
  ];
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
              <CharByChar text="把桌面文件" delay={0.3} /><br />
              <CharByChar text="收进格子里" delay={0.7} className="text-[var(--accent)]" />
            </h1>
            <motion.p
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.5, delay: 1.2 }}
              className="text-xl text-[var(--secondary)] mb-10 max-w-xl mx-auto leading-relaxed"
            >
              Windows 11 桌面整理工具。创建格子收纳文件、映射文件夹、管理剪贴板。
            </motion.p>
            <motion.div
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.5, delay: 1.4 }}
              className="flex flex-wrap justify-center gap-4"
            >
              <Link href="/download" className="fluent-button fluent-button-primary-shimmer text-lg px-10 py-4">免费下载</Link>
              <Link href="/features" className="fluent-button-secondary text-lg px-10 py-4">了解功能</Link>
            </motion.div>
            <motion.p
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              transition={{ duration: 0.5, delay: 1.6 }}
              className="text-sm text-[var(--secondary)] mt-6"
            >
              v1.1.4 · Windows 11/10 · 免费开源
            </motion.p>
          </motion.div>
        </div>
      </section>

      {/* Stats Bar */}
      <section className="py-8 px-4 sm:px-6 lg:px-8 border-y border-[var(--card-border)] bg-[var(--card-background)]">
        <div className="max-w-4xl mx-auto">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-8">
            {stats.map((s, i) => (
              <motion.div
                key={i}
                initial={{ opacity: 0, y: 10 }}
                whileInView={{ opacity: 1, y: 0 }}
                viewport={{ once: true }}
                transition={{ delay: i * 0.1 }}
                className="text-center"
              >
                <div className="text-2xl sm:text-3xl font-bold text-[var(--accent)]">
                  <CountUp target={s.value} suffix={s.suffix} />
                </div>
                <div className="text-sm text-[var(--secondary)] mt-1">{s.label}</div>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* Product Screenshots */}
      <section className="px-4 sm:px-6 lg:px-8 pb-24">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 40 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true, margin: "-50px" }} transition={{ duration: 0.8, ease: [0.16, 1, 0.3, 1] }} className="relative">
            <div className="absolute inset-0 bg-gradient-to-b from-[var(--accent)]/10 to-transparent rounded-3xl blur-3xl" />
            <ParallaxImage src="/screenshots/product-cover-1280x720.png" alt="DeskBox 界面截图" width={1280} height={720} className="relative rounded-2xl border border-[var(--card-border)] shadow-2xl overflow-hidden" />
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mt-4">
            <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: 0.1 }}>
              <ParallaxImage src="/screenshots/widget-light.png" alt="DeskBox 浅色模式" width={640} height={400} className="rounded-xl border border-[var(--card-border)] shadow-lg overflow-hidden" />
            </motion.div>
            <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: 0.2 }}>
              <ParallaxImage src="/screenshots/widget-dark.png" alt="DeskBox 深色模式" width={640} height={400} className="rounded-xl border border-[var(--card-border)] shadow-lg overflow-hidden" />
            </motion.div>
          </div>
        </div>
      </section>

      {/* Workflow Demo */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="四步开始整理" /></h2>
            <p className="text-[var(--secondary)] text-lg">从安装到上手，只需要一分钟</p>
          </motion.div>
          <div className="relative">
            <div className="hidden md:block absolute left-1/2 top-0 bottom-0 w-px bg-[var(--card-border)]" />
            <div className="space-y-12 md:space-y-0">
              {workflowSteps.map((step, i) => (
                <motion.div
                  key={i}
                  initial={{ opacity: 0, x: i % 2 === 0 ? -30 : 30 }}
                  whileInView={{ opacity: 1, x: 0 }}
                  viewport={{ once: true, margin: "-50px" }}
                  transition={{ duration: 0.6, delay: i * 0.1 }}
                  className={`relative md:flex items-center ${i % 2 === 0 ? "md:flex-row" : "md:flex-row-reverse"}`}
                >
                  <div className={`md:w-1/2 ${i % 2 === 0 ? "md:pr-12 md:text-right" : "md:pl-12"}`}>
                    <div className="fluent-card">
                      <div className="text-[var(--accent)] font-mono text-sm font-bold mb-2">STEP {step.step}</div>
                      <h3 className="text-lg font-semibold mb-2">{step.title}</h3>
                      <p className="text-[var(--secondary)] text-sm leading-relaxed">{step.desc}</p>
                    </div>
                  </div>
                  <div className="hidden md:flex absolute left-1/2 -translate-x-1/2 w-10 h-10 rounded-full bg-[var(--accent)] text-white items-center justify-center text-sm font-bold z-10 shadow-lg">
                    {step.step}
                  </div>
                  <div className="md:w-1/2" />
                </motion.div>
              ))}
            </div>
          </div>
        </div>
      </section>

      {/* Scenarios */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5 }} className="mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="用在这些场景" /></h2>
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

      {/* Tech Highlights */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="技术亮点" /></h2>
            <p className="text-[var(--secondary)] text-lg">为什么 DeskBox 值得选择</p>
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-6">
            {techHighlights.map((t, i) => (
              <motion.div
                key={i}
                initial={{ opacity: 0, y: 20 }}
                whileInView={{ opacity: 1, y: 0 }}
                viewport={{ once: true }}
                transition={{ delay: i * 0.08 }}
                className="fluent-card group"
              >
                <div className="text-2xl mb-3">{t.icon}</div>
                <h3 className="font-semibold mb-2">{t.title}</h3>
                <p className="text-[var(--secondary)] text-sm leading-relaxed">{t.desc}</p>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* Features Summary */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="核心功能" /></h2>
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

      {/* FAQ */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-3xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="常见问题" /></h2>
            <p className="text-[var(--secondary)] text-lg">关于 DeskBox 的疑问，这里都有答案</p>
          </motion.div>
          <motion.div
            initial={{ opacity: 0, y: 20 }}
            whileInView={{ opacity: 1, y: 0 }}
            viewport={{ once: true }}
            className="space-y-3"
          >
            {faqs.map((faq, i) => (
              <FAQItem key={i} question={faq.question} answer={faq.answer} />
            ))}
          </motion.div>
        </div>
      </section>

      {/* CTA */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-3xl mx-auto text-center">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5 }}>
            <h2 className="text-3xl font-bold mb-3"><CharByChar text="免费下载" /></h2>
            <p className="text-[var(--secondary)] mb-8">21 MB · Windows 11/10 · 运行时依赖自动安装</p>
            <Link href="/download" className="fluent-button fluent-button-primary-shimmer text-lg px-10 py-4">下载 DeskBox v1.1.4</Link>
          </motion.div>
        </div>
      </section>
    </div>
  );
}
