import { forwardRef, type ButtonHTMLAttributes, type ReactElement, type ReactNode, cloneElement } from "react";
import type React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/cn";

const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary/90",
        outline:
          "border border-border bg-transparent hover:bg-muted text-foreground",
        ghost: "hover:bg-muted text-foreground",
        destructive: "bg-red-600 text-white hover:bg-red-700",
        link: "text-primary underline-offset-4 hover:underline",
      },
      size: {
        default: "h-10 px-4 py-2",
        sm: "h-8 rounded px-3 text-xs",
        lg: "h-11 rounded-md px-8",
        icon: "h-10 w-10",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  },
);

type ButtonOwnProps = VariantProps<typeof buttonVariants> & {
  /**
   * Render the button as a different element by passing a JSX element here.
   * The element's props provide defaults; the caller's props (including `children`,
   * `onClick`, etc.) override them. The forwarded ref points to the rendered element
   * — note that HTML attributes like `disabled` have no semantic effect on non-button
   * elements; consumers must add `aria-disabled` / `tabIndex={-1}` manually for those.
   * Use this instead of `asChild` to stay compatible with `@base-ui/react`.
   */
  render?: ReactElement<{ className?: string; children?: ReactNode }>;
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & ButtonOwnProps;

export const Button = forwardRef<HTMLElement, ButtonProps>(function Button(
  { className, variant, size, render, children, ...props },
  ref,
) {
  const merged = cn(buttonVariants({ variant, size }), className);

  if (render) {
    const renderClassName = render.props.className;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    return cloneElement(render as ReactElement<any>, {
      // Element-provided defaults first (e.g. href, target, type) ...
      ...render.props,
      // ... then caller props override (onClick, aria-*, data-*, etc.) ...
      ...props,
      // ... children passed explicitly because they were destructured out of props ...
      children: children ?? render.props.children,
      // ... ref forwarded so consumers retain access to the actual rendered DOM node ...
      ref,
      // ... className merged from both sides last so the variant classes don't get
      // overwritten by either side's raw className.
      className: cn(merged, renderClassName),
    });
  }

  return (
    <button ref={ref as React.Ref<HTMLButtonElement>} className={merged} {...props}>
      {children}
    </button>
  );
});

export { buttonVariants };
