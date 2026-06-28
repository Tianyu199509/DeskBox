import Image from "next/image";
import Link from "next/link";

export function Footer() {
  return (
    <footer className="border-t border-[var(--card-border)] bg-[var(--card-background)]">
      <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-12">
        <div className="grid grid-cols-1 md:grid-cols-4 gap-8">
          <div className="md:col-span-1">
            <div className="flex items-center gap-2.5 mb-4">
              <Image src="/deskbox-logo-static.svg" alt="DeskBox" width={28} height={28} />
              <span className="font-semibold text-lg tracking-tight">DeskBox</span>
            </div>
            <p className="text-[var(--secondary)] text-sm">轻量级 Windows 11 桌面整理工具，用桌面格子帮你收纳文件、映射文件夹、管理剪贴板。</p>
          </div>
          <div>
            <h3 className="font-semibold mb-4">产品</h3>
            <ul className="space-y-2 text-sm">
              <li><Link href="/features" className="text-[var(--secondary)] hover:text-[var(--foreground)] transition-colors">功能介绍</Link></li>
              <li><Link href="/download" className="text-[var(--secondary)] hover:text-[var(--foreground)] transition-colors">下载</Link></li>
              <li><Link href="/changelog" className="text-[var(--secondary)] hover:text-[var(--foreground)] transition-colors">更新日志</Link></li>
            </ul>
          </div>
          <div>
            <h3 className="font-semibold mb-4">社区</h3>
            <ul className="space-y-2 text-sm">
              <li><a href="https://github.com/Tianyu199509/DeskBox" target="_blank" rel="noopener noreferrer" className="text-[var(--secondary)] hover:text-[var(--foreground)] transition-colors">GitHub</a></li>
              <li><Link href="/about" className="text-[var(--secondary)] hover:text-[var(--foreground)] transition-colors">关于</Link></li>
            </ul>
          </div>
          <div>
            <h3 className="font-semibold mb-4">关注公众号</h3>
            <p className="text-[var(--secondary)] text-sm mb-3">大雨实验室</p>
            <Image src="/wechat-qrcode.jpg" alt="大雨实验室微信公众号" width={120} height={120} className="rounded-lg border border-[var(--card-border)]" />
          </div>
        </div>
        <div className="mt-8 pt-8 border-t border-[var(--card-border)] flex flex-col sm:flex-row justify-between items-center gap-4">
          <p className="text-[var(--secondary)] text-sm">© {new Date().getFullYear()} DeskBox. MIT License.</p>
          <p className="text-[var(--secondary)] text-sm">Built with WinUI 3 & .NET 8</p>
        </div>
      </div>
    </footer>
  );
}
