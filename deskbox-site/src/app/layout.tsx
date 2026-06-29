import type { Metadata } from "next";
import "./globals.css";
import { Navbar } from "@/components/Navbar";
import { Footer } from "@/components/Footer";
import { BackToTop } from "@/components/BackToTop";

export const metadata: Metadata = {
  title: "DeskBox - 轻量级 Windows 11 桌面整理工具",
  description:
    "DeskBox 是一个基于 WinUI 3 的 Windows 11 桌面整理工具，用轻量桌面格子帮你收纳文件、映射文件夹、管理剪贴板。",
  keywords: [
    "桌面整理", "Windows 11", "桌面格子", "文件管理", "桌面小组件", "DeskBox",
    "desktop organizer", "Windows 11 widget", "file manager", "clipboard manager",
    "桌面文件收纳", "剪贴板管理器", "桌面文件夹映射", "Windows 桌面整理工具",
  ],
  openGraph: {
    title: "DeskBox - 轻量级 Windows 11 桌面整理工具",
    description: "用轻量桌面格子帮你收纳文件、映射文件夹、管理剪贴板。",
    type: "website",
    locale: "zh_CN",
  },
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="zh-CN">
      <head>
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{
            __html: JSON.stringify({
              "@context": "https://schema.org",
              "@type": "SoftwareApplication",
              name: "DeskBox",
              operatingSystem: "Windows 11, Windows 10",
              applicationCategory: "UtilitiesApplication",
              offers: { "@type": "Offer", price: "0", priceCurrency: "USD" },
              softwareVersion: "1.1.10",
              description: "轻量级 Windows 11 桌面整理工具，用桌面格子收纳文件、映射文件夹、管理剪贴板。",
              url: "https://github.com/Tianyu199509/DeskBox",
              downloadUrl: "https://github.com/Tianyu199509/DeskBox/releases",
              author: { "@type": "Person", name: "朱天雨" },
            }),
          }}
        />
      </head>
      <body className="min-h-screen flex flex-col">
        <Navbar />
        <main className="flex-1">{children}</main>
        <Footer />
        <BackToTop />
      </body>
    </html>
  );
}
