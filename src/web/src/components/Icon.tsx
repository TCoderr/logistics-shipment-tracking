import type { ReactElement } from "react";

// Şekiller design/operations-preview.html ile aynıdır. İkonlar süs değil durum şeklidir; metin daima yanında durur.
const shapes = {
  created: <circle cx="8" cy="8" r="5" />,
  athub: <rect x="3.5" y="3.5" width="9" height="9" rx="1" />,
  transit: <path d="M2.5 8h10M9 4.5 12.5 8 9 11.5" />,
  ofd: <path d="M1.5 8h7M5.5 4.5 9 8l-3.5 3.5M8.5 4.5 12 8l-3.5 3.5" />,
  failed: (
    <>
      <path d="M8 2.5 14 13H2Z" />
      <path d="M8 6.5v3M8 11.3v.2" />
    </>
  ),
  delivered: <path d="m3 8.5 3.2 3L13 4.5" />,
  returning: <path d="M13.5 8h-10M7 4.5 3.5 8 7 11.5" />,
  returned: <path d="M13.5 8H5M8.5 4.5 5 8l3.5 3.5M2.5 3.5v9" />,
  cancelled: <path d="m4 4 8 8M12 4l-8 8" />,
  clock: (
    <>
      <circle cx="8" cy="8" r="5.5" />
      <path d="M8 5v3.2l2 1.3" />
    </>
  ),
  flag: <path d="M4 14V2.5M4 3h7.5L10 6l1.5 3H4" />,
  search: (
    <>
      <circle cx="7" cy="7" r="4.5" />
      <path d="m10.5 10.5 3 3" />
    </>
  ),
  diamond: <path d="M8 2.5 13.5 8 8 13.5 2.5 8Z" />,
  lock: (
    <>
      <rect x="3.5" y="7" width="9" height="6.5" rx="1" />
      <path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2" />
    </>
  ),
  plug: <path d="M6 2.5v3M10 2.5v3M4.5 5.5h7v2a3.5 3.5 0 0 1-7 0ZM8 11v2.5" />,
  info: (
    <>
      <circle cx="8" cy="8" r="5.5" />
      <path d="M8 7.2v3.6M8 5.2v.2" />
    </>
  ),
  dot: <circle cx="8" cy="8" r="3" />,
} satisfies Record<string, ReactElement>;

export type IconName = keyof typeof shapes;

export function Icon({ name }: { name: IconName }) {
  return (
    <svg className="icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      {shapes[name]}
    </svg>
  );
}
