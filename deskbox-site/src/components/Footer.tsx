import Image from "next/image";
import Link from "next/link";

export function Footer() {
  return (
    <footer>
      <div className="h-px bg-gradient-to-r from-transparent via-[var(--accent)]/20 to-transparent" />
      <div className="bg-[var(--card-background)]/50 backdrop-blur-sm">
        <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-14">
          <div className="grid grid-cols-1 md:grid-cols-4 gap-10">
            <div className="md:col-span-1">
              <div className="flex items-center gap-2.5 mb-4">
                <Image src="/deskbox-logo-static.svg" alt="DeskBox" width={28} height={28} />
                <span className="font-semibold text-lg tracking-tight">DeskBox</span>
              </div>
              <p className="text-[var(--secondary)] text-sm leading-relaxed">轻量级 Windows 11 桌面整理工具，用桌面格子帮你收纳文件、映射文件夹、管理剪贴板。</p>
            </div>
            <div>
              <h3 className="font-semibold mb-4 text-sm uppercase tracking-wider text-[var(--secondary)]">产品</h3>
              <ul className="space-y-3 text-sm">
                <li><Link href="/features" className="text-[var(--foreground)] hover:text-[var(--accent)] transition-colors">功能介绍</Link></li>
                <li><Link href="/download" className="text-[var(--foreground)] hover:text-[var(--accent)] transition-colors">下载</Link></li>
                <li><Link href="/roadmap" className="text-[var(--foreground)] hover:text-[var(--accent)] transition-colors">路线图</Link></li>
                <li><Link href="/changelog" className="text-[var(--foreground)] hover:text-[var(--accent)] transition-colors">更新日志</Link></li>
              </ul>
            </div>
            <div>
              <h3 className="font-semibold mb-4 text-sm uppercase tracking-wider text-[var(--secondary)]">社区</h3>
              <ul className="space-y-3 text-sm">
                <li><a href="https://github.com/Tianyu199509/DeskBox" target="_blank" rel="noopener noreferrer" className="text-[var(--foreground)] hover:text-[var(--accent)] transition-colors">GitHub</a></li>
                <li><Link href="/about" className="text-[var(--foreground)] hover:text-[var(--accent)] transition-colors">关于</Link></li>
              </ul>
            </div>
            <div>
              <h3 className="font-semibold mb-4 text-sm uppercase tracking-wider text-[var(--secondary)]">关注公众号</h3>
              <p className="text-[var(--foreground)] text-sm mb-3 font-medium">大雨实验室</p>
              <Image src="/wechat-qrcode.jpg" alt="大雨实验室微信公众号" width={120} height={120} className="rounded-lg border border-[var(--card-border)]" />
            </div>
          </div>
          <div className="mt-10 pt-8 border-t border-[var(--card-border)] flex flex-col sm:flex-row justify-between items-center gap-4">
            <p className="text-[var(--secondary)] text-sm">© {new Date().getFullYear()} DeskBox. GPLv3 License.</p>
            <p className="text-[var(--secondary)] text-sm">Made with ❤️ and AI</p>
          </div>
        </div>
      </div>
    </footer>
  );
}
