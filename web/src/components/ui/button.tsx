import { forwardRef, type ButtonHTMLAttributes, type ReactElement, cloneElement } from "react";
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
   * The element receives the merged className and forwards refs/props.
   * Use this instead of `asChild` to stay compatible with `@base-ui/react`.
   */
  render?: ReactElement<{ className?: string }>;
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & ButtonOwnProps;

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { className, variant, size, render, children, ...props },
  ref,
) {
  const merged = cn(buttonVariants({ variant, size }), className);

  if (render) {
    const renderClassName = render.props.className;
    return cloneElement(render, {
      ...props,
      ...render.props,
      className: cn(merged, renderClassName),
    });
  }

  return (
    <button ref={ref} className={merged} {...props}>
      {children}
    </button>
  );
});

export { buttonVariants };
