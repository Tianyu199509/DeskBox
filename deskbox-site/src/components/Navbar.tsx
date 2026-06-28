"use client";

import Image from "next/image";
import Link from "next/link";
import { useState } from "react";
import { usePathname } from "next/navigation";
import { motion, AnimatePresence } from "framer-motion";

const navItems = [
  { href: "/", label: "首页" },
  { href: "/features", label: "功能" },
  { href: "/download", label: "下载" },
  { href: "/changelog", label: "更新日志" },
  { href: "/about", label: "关于" },
];

export function Navbar() {
  const [isOpen, setIsOpen] = useState(false);
  const pathname = usePathname();

  const isActive = (href: string) => {
    if (href === "/") return pathname === "/";
    return pathname.startsWith(href);
  };

  return (
    <nav className="sticky top-0 z-50 backdrop-blur-xl bg-[var(--background)]/70 border-b border-[var(--card-border)]">
      <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between h-16">
          <Link href="/" className="flex items-center gap-2.5">
            <Image src="/deskbox-logo-static.svg" alt="DeskBox" width={28} height={28} />
            <span className="font-semibold text-lg tracking-tight">DeskBox</span>
          </Link>
          <div className="hidden md:flex items-center gap-1">
            {navItems.map((item) => (
              <Link
                key={item.href}
                href={item.href}
                className={`px-3 py-2 rounded-lg text-sm font-medium transition-all duration-150 ${
                  isActive(item.href)
                    ? "text-[var(--accent)] bg-[var(--accent-light)]"
                    : "text-[var(--secondary)] hover:text-[var(--foreground)] hover:bg-[var(--card-border)]/50"
                }`}
              >
                {item.label}
              </Link>
            ))}
            <Link href="/download" className="fluent-button text-sm py-2 px-5 ml-3">下载</Link>
          </div>
          <button className="md:hidden p-2 rounded-lg hover:bg-[var(--card-border)] transition-colors" onClick={() => setIsOpen(!isOpen)} aria-label="Toggle menu">
            <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              {isOpen ? <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /> : <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h16" />}
            </svg>
          </button>
        </div>
      </div>
      <AnimatePresence>
        {isOpen && (
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.2 }} className="md:hidden border-t border-[var(--card-border)] overflow-hidden">
            <div className="px-4 py-4 space-y-2">
              {navItems.map((item) => (
                <Link
                  key={item.href}
                  href={item.href}
                  className={`block py-3 px-4 rounded-lg transition-all ${
                    isActive(item.href)
                      ? "text-[var(--accent)] bg-[var(--accent-light)] font-medium"
                      : "text-[var(--secondary)] hover:text-[var(--foreground)] hover:bg-[var(--card-border)]"
                  }`}
                  onClick={() => setIsOpen(false)}
                >
                  {item.label}
                </Link>
              ))}
              <Link href="/download" className="block fluent-button text-center mt-4" onClick={() => setIsOpen(false)}>下载</Link>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </nav>
  );
}
